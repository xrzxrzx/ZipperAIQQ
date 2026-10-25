using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Embedding;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Agent.Memory;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Agent.Search;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Apis;
using ZipperAIQQ.Channel.Audio;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.NapCat;
using ZipperAIQQ.Channel.Throttling;
using ZipperAIQQ.Core.Events;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Commands;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Server.External;
using ZipperAIQQ.Server.Feedback;
using ZipperAIQQ.Server.Identity;
using ZipperAIQQ.Server.Logs;
using ZipperAIQQ.Server.Media;
using ZipperAIQQ.Server.Notices;
using ZipperAIQQ.Server.Orchestration;
using ZipperAIQQ.Server.Process;
using ZipperAIQQ.Server.Reply;
using ZipperAIQQ.Server.Search;
using ZipperAIQQ.Server.Scheduler;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Embeddings;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Notices;
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
        // ── M2-B：运行配置（端点 + 角色映射 + 审计），key 加密在 Server 层完成 ──
        services.AddSingleton<IConfigStore, SqliteConfigStore>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IChatClientProvider, ChatClientProvider>();
        services.AddSingleton<RuntimeConfigService>();
        services.AddSingleton<IApiAction, GetConfigAction>();
        services.AddSingleton<IApiAction, SetConfigAction>();
        services.AddSingleton<IApiAction, GetAuditAction>();
        services.AddSingleton<IApiAction, GetSettingsAction>();
        services.AddSingleton<IApiAction, SetSettingsAction>();
        services.AddSingleton<IApiAction, GetStatsAction>();
        services.AddSingleton<IApiAction, GetBehaviorAction>();
        services.AddSingleton<IApiAction, SetBehaviorAction>();
        services.AddSingleton<IApiAction, GetPersonaAction>();
        services.AddSingleton<IApiAction, SetPersonaAction>();
        services.AddSingleton<IApiAction, SetGlobalPersonaAction>();
        services.AddSingleton<IApiAction, ClearPersonaAction>();
        services.AddSingleton<IApiAction, GetOwnerAction>();
        services.AddSingleton<IApiAction, SetOwnerAction>();
        services.AddSingleton<IApiAction, ProbeNapcatAction>();
        services.AddSingleton<IApiAction, GetAccessAction>();
        services.AddSingleton<IApiAction, SetAccessAction>();

        // ── #28：工具 / 命令 / NapCat API 的策略目录与开关 ──
        services.AddSingleton<IExecutionPolicy>(sp =>
        {
            var items = new List<PolicyItem>(ToolCatalog.All);

            items.AddRange(CommandCatalog.All.Select(c =>
                new PolicyItem(PolicyCategory.Command, c.Name, c.Label, c.MinRole)));

            items.AddRange(ChannelApiCatalog.All
                .Where(a => a.ShowInCatalog)
                .Select(a =>
                    new PolicyItem(
                        PolicyCategory.ChannelApi,
                        a.Action,
                        a.Label,
                        a.IsWrite ? Role.Admin : Role.Normal)));

            return new ExecutionPolicy(
                sp.GetRequiredService<IRuntimeSettingsStore>(),
                items,
                sp.GetRequiredService<ILogger>());
        });
        services.AddHostedService<PolicyRefreshService>();
        services.AddSingleton<IApiAction, GetToolCatalogAction>();
        services.AddSingleton<IApiAction, SetToolPolicyAction>();

        // 系统提示词（与人设分开的基础行为规则）
        services.AddSingleton<IApiAction, GetSystemPromptAction>();
        services.AddSingleton<IApiAction, SetSystemPromptAction>();
        services.AddSingleton<IApiAction, ResetSystemPromptAction>();

        // #31：服务端日志检索（只读 logs/zipper-*.log）
        services.AddSingleton<ILogQuery>(sp => new FileLogQuery("logs", sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IApiAction, QueryLogsAction>();

        // #36：重启（优雅停止 + 外部守护拉起）
        services.AddSingleton<IServerRestartService, ServerRestartService>();
        services.AddSingleton<IApiAction, RestartServerAction>();

        // 自我身份（问题记录 02 #3）：通道发现 self_id 后持久化，并提供只读诊断
        services.AddHostedService<SelfIdRecorder>();
        services.AddSingleton<IApiAction, GetSelfInfoAction>();

        // #38：网页搜索（DeepSeek 官方 /v1/tools/search + /v1/tools/fetch）
        services.AddHttpClient();
        services.AddSingleton<IWebSearchProvider>(sp =>
        {
            var configs = sp.GetRequiredService<IConfigStore>();
            var protector = sp.GetRequiredService<ISecretProtector>();

            return new DeepSeekWebSearchProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DeepSeekWebSearchProvider)),
                async ct =>
                {
                    var endpoint = await configs.GetEndpointAsync("primary", ct).ConfigureAwait(false);
                    return endpoint is null ? null : protector.Unprotect(endpoint.KeyCipher);
                },
                options.SearchEnabled,
                options.SearchBaseUrl,
                sp.GetRequiredService<ILogger>());
        });

        // 图片（问题记录 03 #2）：下载字节 + 真实 MIME + GIF/WebP 首帧转 PNG
        services.AddSingleton<IImageFetcher>(sp =>
        {
            var client = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient(nameof(HttpImageFetcher));
            client.Timeout = TimeSpan.FromSeconds(8);
            return new HttpImageFetcher(client, sp.GetRequiredService<ILogger>());
        });
        services.AddSingleton<IImageConverter, SkiaImageConverter>();

        // 主人反馈（问题记录 03 #4）：指定通知抄送到主人私聊
        services.AddSingleton<OwnerFeedbackService>();
        services.AddSingleton<IApiAction, GetFeedbackAction>();
        services.AddSingleton<IApiAction, SetFeedbackAction>();

        // 外部 WS（问题记录 03 #5）：反向连入 + 正向连出
        services.AddSingleton<ExternalWsHub>();
        services.AddSingleton<ExternalCommandHandler>();
        services.AddHostedService<ForwardWsClient>();

        services.AddSingleton<ActionDispatcher>();
        services.AddSingleton<EventBroadcaster>();

        // ── M2-E：回复触发模式（FR-11）：every / mention / jev（判断失败降级 mention）──
        services.AddSingleton<IRuntimeSettingsStore, SqliteRuntimeSettingsStore>();
        // decisions 模型（Jev）判断客户端：OpenRouter /api/alpha/decisions
        services.AddHttpClient();
        services.AddSingleton(sp => new DecisionsJudgeClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DecisionsJudgeClient)),
            sp.GetRequiredService<Serilog.ILogger>()));
        services.AddSingleton<IReplyModeDecider, ReplyModeDecider>();

        // ── M2-E：通知落库 + 可配置响应（FR-18，默认只记录）──
        services.AddSingleton<INoticeStore, SqliteNoticeStore>();
        services.AddSingleton<NoticeResponder>();

        // ── M2-C：命令系统（新增命令 = 新增一个类 + 这里加一行）──
        services.AddSingleton<ICommand, HelpCommand>();
        services.AddSingleton<ICommand, OwnerCommand>();
        services.AddSingleton<ICommand, StatusCommand>();
        services.AddSingleton<ICommand, PersonaCommand>();
        services.AddSingleton<ICommand, RememberCommand>();
        services.AddSingleton<ICommand, SearchCommand>();
        services.AddSingleton<ICommand, CompactCommand>();
        services.AddSingleton<ICommand, ResetCommand>();
        services.AddSingleton<ICommand, ForgetCommand>();
        services.AddSingleton<ICommand, ExportCommand>();
        services.AddSingleton<ICommand, PurgeCommand>();
        services.AddSingleton<ICommand, ModeCommand>();
        services.AddSingleton<ICommand, NoticeCommand>();
        services.AddSingleton<CommandRouter>();

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
        // 内层通道单独注册：IContactDirectory（好友/群/群成员查询）需要它，
        // 而消息发送要过外层节流 —— 两者拿的不是同一个对象。
        services.AddSingleton<NapCatChannel>(sp => new NapCatChannel(
            sp.GetRequiredService<IClientTransport>(),
            sp.GetRequiredService<IChatFilter>(),
            new NapCatOptions
            {
                WebSocketUrl = options.NapCatWebSocketUrl,
                AccessToken = options.NapCatAccessToken,
            },
            sp.GetRequiredService<ILogger>(),
            new SpeechOptions { Enabled = options.SttEnabled }));

        services.AddSingleton<IContactDirectory>(sp => sp.GetRequiredService<NapCatChannel>());

        // 会话显示名（#2）：好友昵称 / 群名，带 5 分钟缓存
        services.AddSingleton<ISessionNameResolver, SessionNameResolver>();

        // 语音转写：契约在 Channel，实现是 NapCat 的 fetch_ptt_text（Q5：不接本地模型）
        services.AddSingleton<ISpeechTranscriber>(sp => sp.GetRequiredService<NapCatChannel>().SpeechTranscriber);

        // 主人放行范围（私聊 + 主人所在的群）：后台刷新，失败自动重试
        services.AddSingleton<OwnerScopeResolver>();
        services.AddSingleton<IOwnerScope>(sp => sp.GetRequiredService<OwnerScopeResolver>());
        services.AddHostedService(sp => sp.GetRequiredService<OwnerScopeResolver>());

        services.AddSingleton<IMessageChannel>(sp =>
            // 所有出站消息都过这里：全局最短间隔 + 连发冷却，跨会话共享
            new ThrottledMessageChannel(
                sp.GetRequiredService<NapCatChannel>(),
                options.SendIntervalMs,
                options.MaxConsecutive,
                options.CooldownMs));

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

        // ── 模型：由运行配置驱动，可热替换（FR-8.4）──
        // 具体客户端由 IChatClientProvider 按库里的配置创建（ChatClientProvider），
        // 这里不再直接注册 IChatClient —— 那是个不可变单例，换端点只能重启。

        // ── 摘要（温层）：在后台把滑出热窗的旧对话压成一段话 ──
        services.AddSingleton(sp => new SummaryService(
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetRequiredService<IMessageStore>(),
            sp.GetRequiredService<ISummaryStore>(),
            sp.GetRequiredService<IMetricsStore>(),
            sp.GetRequiredService<ILogger>()));

        // ── Agent 池：一个聊天一个实例（D9）──
        services.AddSingleton<IChatAgentPool>(sp =>
        {
            var deps = new ChatAgentDependencies
            {
                Client = sp.GetRequiredService<IChatClientProvider>(),
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
                Policy = sp.GetRequiredService<IExecutionPolicy>(),
                WebSearch = sp.GetService<IWebSearchProvider>(),
                ImageFetcher = sp.GetService<IImageFetcher>(),
                ImageConverter = sp.GetService<IImageConverter>(),
                Roles = sp.GetRequiredService<IRoleResolver>(),
                Workspace = sp.GetRequiredService<WorkspacePathResolver>(),
                FileMaxBytes = options.FileMaxBytes,
                Schedules = sp.GetRequiredService<IScheduleStore>(),
                Options = BuildAgentOptions(options),
                Logger = sp.GetRequiredService<ILogger>(),
            };

            var configs = sp.GetRequiredService<Storage.Config.IConfigStore>();
            var settings = sp.GetRequiredService<Storage.Settings.IRuntimeSettingsStore>();
            return new ChatAgentPool(chat => new ChatAgent(chat.SessionId, deps, configs, settings));
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
            ? new ChatFilterOptions { Whitelist = options.Whitelist }
            // 没配：两份名单都空 = 全放行（首次跑通方便；生产请显式配名单）
            : new ChatFilterOptions();

    private static ChatAgentOptions BuildAgentOptions(ServerOptions options) => new()
    {
        // 人设由 IPersonaStore 提供；这里只是「连人设文件都读不到」时的兜底，
        // 保证永远有一条 system，不会出现没有角色的空壳对话。
        FallbackSystemPrompt = "你是一个聊天助手，用简短自然的中文回复。",

        // 这三个以前**没接**（用了 ChatAgentOptions 的默认 0 / 20）：
        // 也就是说「发送限速=300ms」只打印在启动日志里，实际根本没生效。
        MinSendIntervalMs = options.SendIntervalMs,
        HotWindowMessages = options.HotWindowMessages,
        SelfId = options.SelfId,
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
