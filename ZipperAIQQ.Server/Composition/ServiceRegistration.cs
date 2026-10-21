using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Embedding;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Agent.Memory;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.NapCat;
using ZipperAIQQ.Channel.Throttling;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Orchestration;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Embeddings;
using ZipperAIQQ.Storage.Messages;
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

        // ── 存储 ──
        services.AddSingleton<ISqliteDatabase>(sp =>
            new SqliteDatabase(connectionString, sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IMessageStore, SqliteMessageStore>();
        services.AddSingleton<IEmbeddingStore, SqliteEmbeddingStore>();
        services.AddSingleton<ISummaryStore, SqliteSummaryStore>();

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
            sp.GetRequiredService<ILogger>()));

        // ── 表情目录 ──
        services.AddSingleton(_ => FaceCatalog.Load(Path.GetFullPath(options.FacesPath)));

        // ── 模型 ──
        services.AddSingleton<IChatClient>(_ => ChatClientFactory.Create(
            new LlmEndpoint("primary", options.LlmBaseUrl, options.LlmModel, options.LlmApiKind),
            options.LlmApiKey));

        // ── 摘要（温层）：在后台把滑出热窗的旧对话压成一段话 ──
        services.AddSingleton(sp => new SummaryService(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<IMessageStore>(),
            sp.GetRequiredService<ISummaryStore>(),
            sp.GetRequiredService<ILogger>()));

        // ── Agent 池：一个聊天一个实例（D9）──
        services.AddSingleton<IChatAgentPool>(sp =>
        {
            var client = sp.GetRequiredService<IChatClient>();
            var channel = sp.GetRequiredService<IMessageChannel>();
            var store = sp.GetRequiredService<IMessageStore>();
            var faces = sp.GetRequiredService<FaceCatalog>();
            var retriever = sp.GetRequiredService<MemoryRetriever>();
            var summaries = sp.GetRequiredService<SummaryService>();
            var summaryStore = sp.GetRequiredService<ISummaryStore>();
            var logger = sp.GetRequiredService<ILogger>();
            var agentOptions = BuildAgentOptions();

            return new ChatAgentPool(chat => new ChatAgent(
                chat.SessionId, client, channel, store, faces, retriever,
                summaries, summaryStore, agentOptions, logger));
        });

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
        SystemPrompt =
            "你是一个角色扮演助手，在聊天软件里和人对话。" +
            "**你必须调用 send_message 工具来把话发出去** —— 直接输出文本对方是看不到的。" +
            "可以一次发多条（像真人把一句话分几条发）。回复要简短自然。" +
            "**发表情时**：先调用 list_faces 查询（不传关键词看常用，传关键词搜名称/标签），" +
            "然后把 [表情:名字] 写进消息里。**单独一条只写 [表情:名字] 会作为大表情发送**（有特效）。",
    };
}
