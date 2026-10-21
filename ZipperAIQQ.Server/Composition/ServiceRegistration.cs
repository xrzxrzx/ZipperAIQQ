using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Embedding;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Agent.Memory;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.NapCat;
using ZipperAIQQ.Channel.Throttling;
using ZipperAIQQ.Core.Events;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Orchestration;
using ZipperAIQQ.Server.Scheduler;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Embeddings;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;
using ZipperAIQQ.Storage.Personas;
using ZipperAIQQ.Storage.Schedules;
using ZipperAIQQ.Storage.Summaries;
using ZipperAIQQ.Transport.Client;

namespace ZipperAIQQ.Server.Composition;

/// <summary>
/// 业务服务的装配。**这是唯一的组合根**，所有 new 都发生在这里或它调用的工厂里。
/// </summary>
/// <remarks>
/// 从 <c>Program.cs</c> 抽出来的理由很实际：装配逻辑混在启动流程里时，
/// 「改一个组件要动哪个文件」和「启动顺序」纠缠在一起，两边都难改。
/// 现在启动流程只关心顺序，装配只关心图。
/// </remarks>
public static class ServiceRegistration
{
    public static IServiceCollection AddZipperServices(
        this IServiceCollection services,
        ServerOptions options,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // 配置本身也进容器：action / hosted service 需要读模型名、监听地址等
        services.AddSingleton(options);

        // ── 事件总线（Agent 事件：漂移 / 降级；M2 客户端接 WS 后订阅同一批事件）──
        services.AddSingleton<IEventBus>(sp => new EventBus(sp.GetRequiredService<ILogger>()));

        // ── M2-A：HTTP / WS 端点 ──
        // HttpAccessPolicy 构造时校验：非 loopback 无 token 直接抛，拒绝启动
        services.AddSingleton(_ => new HttpAccessPolicy(options.HttpAddress, options.HttpToken));
        services.AddSingleton<IApiAction, PingAction>();
        services.AddSingleton<IApiAction, GetStatusAction>();
        services.AddSingleton<IApiAction, ListSessionsAction>();
        services.AddSingleton<IApiAction, GetMessagesAction>();
        services.AddSingleton<IApiAction, GetMetricsAction>();
        services.AddSingleton<ActionDispatcher>();
        services.AddSingleton<EventBroadcaster>();

        // ── 权限与工作区（M1-E / FR-6 / FR-7）──
        // PermissionGate 构造时就校验硬底线：越界配置直接拒绝启动（FR-6.6）
        services.AddSingleton<IPermissionGate>(_ => new PermissionGate());
        services.AddSingleton<IRoleResolver>(_ => new ConfiguredRoleResolver(options.Admins, options.ToolWhitelist));
        services.AddSingleton(_ => new WorkspacePathResolver(options.WorkspacePath));

        // ── 存储 ──
        services.AddSingleton<ISqliteDatabase>(sp =>
            new SqliteDatabase(connectionString, sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IMessageStore, SqliteMessageStore>();
        services.AddSingleton<IEmbeddingStore, SqliteEmbeddingStore>();
        services.AddSingleton<ISummaryStore, SqliteSummaryStore>();
        services.AddSingleton<IMetricsStore, SqliteMetricsStore>();
        services.AddSingleton<IScheduleStore, SqliteScheduleStore>();

        // ── 接收过滤：白名单空 = 全放行仅在"没配置白名单"时启用，避免默认什么都收 ──
        services.AddSingleton<IChatFilter>(_ => new ChatFilter(BuildFilterOptions(options)));

        // ── 通道（内层 NapCat + 外层节流）──
        services.AddSingleton<IClientTransport>(sp =>
            new ClientTransport(sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IMessageChannel>(sp =>
        {
            var inner = new NapCatChannel(
                sp.GetRequiredService<IClientTransport>(),
                sp.GetRequiredService<IChatFilter>(),
                new NapCatOptions
                {
                    WebSocketUrl = options.NapCatWebSocketUrl,
                    AccessToken = options.NapCatAccessToken,
                },
                sp.GetRequiredService<ILogger>());

            // 所有出站消息都过这里：全局最短间隔 + 连发冷却，跨会话共享
            return new ThrottledMessageChannel(
                inner, options.SendIntervalMs, options.MaxConsecutive, options.CooldownMs);
        });

        // ── 向量与长期记忆 ──
        services.AddSingleton<IEmbeddingService>(_ =>
            new OnnxEmbeddingService(options.EmbeddingModelDirectory));
        services.AddSingleton(sp => new MemoryRetriever(
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<IEmbeddingStore>(),
            sp.GetRequiredService<IMessageStore>(),
            sp.GetRequiredService<IMetricsStore>(),
            sp.GetRequiredService<ILogger>()));

        // ── 表情目录 ──
        services.AddSingleton(_ => FaceCatalog.Load(Path.GetFullPath(options.FacesPath)));

        // ── 人设（D4/D12）：全局来自文件，会话级可在运行时覆盖 ──
        var globalPersona = LoadGlobalPersona(options.PersonaPath);
        services.AddSingleton<IPersonaStore>(sp => new DefaultPersonaStore(
            sp.GetRequiredService<ISqliteDatabase>(), globalPersona));

        // ── 模型 ──
        services.AddSingleton<IChatClient>(_ => ChatClientFactory.Create(
            new LlmEndpoint("primary", options.LlmBaseUrl, options.LlmModel, options.LlmApiKind),
            options.LlmApiKey));

        // ── 摘要（温层）：在后台把滑出热窗的旧对话压成一段话 ──
        services.AddSingleton(sp => new SummaryService(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<IMessageStore>(),
            sp.GetRequiredService<ISummaryStore>(),
            sp.GetRequiredService<IMetricsStore>(),
            sp.GetRequiredService<ILogger>()));

        // ── Agent 池：一个聊天一个实例（D9）──
        services.AddSingleton<IChatAgentPool>(sp =>
        {
            var deps = new ChatAgentDependencies
            {
                Client = sp.GetRequiredService<IChatClient>(),
                Channel = sp.GetRequiredService<IMessageChannel>(),
                Store = sp.GetRequiredService<IMessageStore>(),
                Faces = sp.GetRequiredService<FaceCatalog>(),
                Retriever = sp.GetRequiredService<MemoryRetriever>(),
                Summaries = sp.GetRequiredService<SummaryService>(),
                SummaryStore = sp.GetRequiredService<ISummaryStore>(),
                Personas = sp.GetRequiredService<IPersonaStore>(),
                Metrics = sp.GetRequiredService<IMetricsStore>(),
                Events = sp.GetRequiredService<IEventBus>(),
                PermissionGate = sp.GetRequiredService<IPermissionGate>(),
                Roles = sp.GetRequiredService<IRoleResolver>(),
                Workspace = sp.GetRequiredService<WorkspacePathResolver>(),
                FileMaxBytes = options.FileMaxBytes,
                Schedules = sp.GetRequiredService<IScheduleStore>(),
                Options = BuildAgentOptions(),
                Logger = sp.GetRequiredService<ILogger>(),
            };

            return new ChatAgentPool(chat => new ChatAgent(chat.SessionId, deps));
        });

        // ── 定时任务轮询（FR-14：数据库即队列，1 分钟精度，启动时补执行）──
        services.AddHostedService<SchedulePoller>();

        // ── 消息路由 ──
        services.AddHostedService<MessageRouter>();

        return services;
    }

    private static ChatFilterOptions BuildFilterOptions(ServerOptions options) =>
        options.Whitelist.Count > 0
            // 配了白名单：只收这些会话
            ? new ChatFilterOptions { Mode = ChatListMode.Whitelist, Entries = options.Whitelist }
            // 没配：黑名单 + 空 = 全放行（首次跑通方便；生产请显式配白名单）
            : new ChatFilterOptions { Mode = ChatListMode.Blacklist, Entries = [] };

    private static ChatAgentOptions BuildAgentOptions() => new()
    {
        // 人设由 IPersonaStore 提供；这里只是「连人设文件都读不到」时的兜底，
        // 保证永远有一条 system，不会出现没有角色的空壳对话。
        FallbackSystemPrompt = "你是一个聊天助手，用简短自然的中文回复。",
    };

    /// <summary>读取全局人设文件；读不到就用兜底文案。</summary>
    private static Persona LoadGlobalPersona(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            var content = File.ReadAllText(fullPath);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return new Persona(Path.GetFileNameWithoutExtension(fullPath), content.Trim());
            }
        }

        return new Persona("fallback", "你是一个聊天助手，用简短自然的中文回复。");
    }
}
