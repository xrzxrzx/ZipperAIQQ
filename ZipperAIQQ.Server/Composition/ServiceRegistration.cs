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
using ZipperAIQQ.Mail;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Mail;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Commands;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Server.Channels;
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
using ZipperAIQQ.Storage.Mails;
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

        // #38：网页搜索（与模型端点独立：自己的 baseUrl + 加密 key）
        services.AddHttpClient();
        services.AddSingleton<SearchOptionsService>();
        services.AddSingleton<IApiAction, GetSearchAction>();
        services.AddSingleton<IApiAction, SetSearchAction>();
        services.AddSingleton<IWebSearchProvider>(sp =>
        {
            var search = sp.GetRequiredService<SearchOptionsService>();

            return new DeepSeekWebSearchProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(DeepSeekWebSearchProvider)),
                ct => search.GetKeyAsync(ct),
                () => search.Current,
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

        // 外部 WS（问题记录 03 #5）：反向连入 + 正向连出（配置可客户端热改）
        services.AddSingleton<ExternalOptionsService>();
        services.AddSingleton<ExternalWsHub>();
        services.AddSingleton<ExternalCommandHandler>();
        services.AddHostedService<ForwardWsClient>();
        services.AddSingleton<IApiAction, GetExternalAction>();
        services.AddSingleton<IApiAction, SetExternalAction>();

        services.AddSingleton<ActionDispatcher>();
        services.AddSingleton<EventBroadcaster>();

        // ── 邮件接入（多平台通道接入 · 决策一 / 九）：IMAP 收信 → 摘要 → 转发给主人 ──
        // 邮件**只收不发**：实现 IMessageSource 而不是 IMessageChannel（邮件接入设计 §2.1）。
        services.AddSingleton<IMailRepository, SqliteMailRepository>();
        services.AddSingleton<IImapSessionFactory, MailKitImapSessionFactory>();
        services.AddSingleton<ICredentialProtector, MailSecretAdapter>();
        services.AddSingleton(sp => new MailSource(
            sp.GetRequiredService<IImapSessionFactory>(),
            sp.GetRequiredService<IMailRepository>(),
            sp.GetRequiredService<ICredentialProtector>(),
            new MailSourceOptions
            {
                InitialDays = options.Mail.InitialDays,
                PollIntervalSeconds = options.Mail.PollIntervalSeconds,
                MaxBodyChars = options.Mail.MaxBodyChars,
            },
            sp.GetRequiredService<ILogger>()));
        // 源**不**注册成 HostedService（实施微调 M12）：由 MailPipeline 在 ExecuteAsync 里
        // StartAsync / StopAsync，与 MessageRouter 启动 NapCatChannel 完全同构 —— 生命周期只有一处。
        // 邮件策略参数进容器：action 与两个服务都直接注入 MailOptions，不引 IOptions<T>。
        services.AddSingleton(options.Mail);
        services.AddSingleton(sp => new MailSummaryService(
            sp.GetRequiredService<IChatClientProvider>(),
            options.Mail,
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IMailSummaryService>(sp => sp.GetRequiredService<MailSummaryService>());
        services.AddSingleton(sp => new MailDigestService(
            sp.GetRequiredService<IRuntimeSettingsStore>(),
            sp.GetRequiredService<IMessageChannel>(),
            sp.GetRequiredService<IMailRepository>(),
            options.Mail,
            sp.GetRequiredService<ILogger>()));
        services.AddHostedService<MailPipeline>();
        services.AddSingleton<IApiAction, ListMailsAction>();
        services.AddSingleton<IApiAction, GetMailAction>();
        services.AddSingleton<IApiAction, ListMailAccountsAction>();
        services.AddSingleton<IApiAction, SaveMailAccountAction>();
        services.AddSingleton<IApiAction, DeleteMailAccountAction>();
        services.AddSingleton<IApiAction, SyncMailAction>();

        // ── 第三方平台接入页（S3-F / 决策七）：平台总览 + 抖音配置读写 ──
        // 邮件账号的增删改直接用上面那批 mail action，不重复造一套。
        services.AddSingleton<IApiAction, GetPlatformsAction>();
        services.AddSingleton<IApiAction, SaveDouyinAction>();

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
        // 角色解析按平台（S3-C）：启动配置（环境变量 / YAML）只喂 qq，其余平台由 Program.cs 从库里灌
        services.AddSingleton<IRoleResolver>(_ =>
            new ConfiguredRoleResolver(options.Admins, options.ToolWhitelist, Access.AccessKeys.DefaultPlatform));
        services.AddSingleton(_ => new WorkspacePathResolver(options.WorkspacePath));

        // ── 存储 ──
        services.AddSingleton<ISqliteDatabase>(sp =>
            new SqliteDatabase(connectionString, sp.GetRequiredService<ILogger>()));
        services.AddSingleton<IMessageStore, SqliteMessageStore>();
        services.AddSingleton<IEmbeddingStore, SqliteEmbeddingStore>();
        services.AddSingleton<ISummaryStore, SqliteSummaryStore>();
        services.AddSingleton<IMetricsStore, SqliteMetricsStore>();
        services.AddSingleton<IScheduleStore, SqliteScheduleStore>();

        // ── 接收过滤（S3-C：按平台分桶）：启动种子只喂 qq（环境变量 / YAML 是单平台时代的写法），
        //    其余平台的名单由 Program.cs 从库里按平台灌入 ──
        services.AddSingleton<IChatFilter>(_ =>
            new ChatFilter(BuildFilterOptions(options), Access.AccessKeys.DefaultPlatform));

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

        // ── 抖音通道（S3-E / 决策二）：sidecar 连入后由 /ws/channel 调 AttachAsync ──
        // 关掉时也注册（端点会 404），只是不进复合通道、不进联系人注册表。
        // 配置走 DouyinOptionsService（S3-F）：客户端可改、立即生效；启动值来自 ServerOptions.Douyin。
        services.AddSingleton(sp => new DouyinOptionsService(
            sp.GetRequiredService<IRuntimeSettingsStore>(),
            options,
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton(sp => new Channels.DouyinChannel(
            sp.GetRequiredService<DouyinOptionsService>().Current.ToOptions(),
            sp.GetRequiredService<ILogger>()));

        // 联系人目录按平台注册（决策四第 6 条）：配置页 / 主人放行范围 / 平台专用工具都只认平台名。
        services.AddSingleton<IContactDirectoryRegistry>(sp =>
        {
            var map = new Dictionary<string, IContactDirectory>(StringComparer.OrdinalIgnoreCase)
            {
                [Access.AccessKeys.DefaultPlatform] = sp.GetRequiredService<IContactDirectory>(),
            };

            if (options.Douyin.Enabled)
            {
                map[Channels.DouyinChannel.PlatformName] = sp.GetRequiredService<Channels.DouyinChannel>();
            }

            return new ContactDirectoryRegistry(map);
        });

        // Agent 层按平台取目录（抖音专用工具用）：同一个注册表，只是接口更窄
        services.AddSingleton<IPlatformContacts>(sp =>
            sp.GetRequiredService<IContactDirectoryRegistry>());

        // 会话显示名（#2）：好友昵称 / 群名，带 5 分钟缓存
        services.AddSingleton<ISessionNameResolver, SessionNameResolver>();

        // 语音转写：契约在 Channel，实现是 NapCat 的 fetch_ptt_text（Q5：不接本地模型）
        services.AddSingleton<ISpeechTranscriber>(sp => sp.GetRequiredService<NapCatChannel>().SpeechTranscriber);

        // 主人放行范围（私聊 + 主人所在的群）：后台刷新，失败自动重试
        services.AddSingleton<OwnerScopeResolver>();
        services.AddSingleton<IOwnerScope>(sp => sp.GetRequiredService<OwnerScopeResolver>());
        services.AddHostedService(sp => sp.GetRequiredService<OwnerScopeResolver>());

        // ── 复合通道（S3-A / 决策三）：入站合并 + 出站按 ChatRef.Platform 路由 ──
        // 只包 IMessageChannel；邮件是 IMessageSource，由 MailPipeline 自己消费（不进这里）。
        // 节流仍包在**最外层**（设计 §S3-A）：那是"全局发送节奏"，与哪个平台无关。
        services.AddSingleton<IMessageChannel>(sp =>
        {
            var channels = new List<IMessageChannel>
            {
                sp.GetRequiredService<NapCatChannel>(),
            };

            if (options.Douyin.Enabled)
            {
                // 抖音是第二个 IMessageChannel：注册位置在复合通道里，上游零改动（决策三验收）
                channels.Add(sp.GetRequiredService<Channels.DouyinChannel>());
            }

            var composite = new Channels.CompositeMessageChannel(
                channels, sp.GetRequiredService<ILogger>());

            // 所有出站消息都过这里：全局最短间隔 + 连发冷却，跨会话共享
            return new ThrottledMessageChannel(
                composite,
                options.SendIntervalMs,
                options.MaxConsecutive,
                options.CooldownMs);
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
                // 平台专用工具（抖音）靠它按平台取联系人目录
                PlatformContacts = sp.GetRequiredService<IPlatformContacts>(),
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
