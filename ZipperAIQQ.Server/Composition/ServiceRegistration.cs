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
using ZipperAIQQ.Server.Access;
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
public static partial class ServiceRegistration
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

        // 主人写入的唯一入口（setOwner action 与 /owner 命令共用）
        services.AddSingleton<OwnerService>();

        // 自我身份（问题记录 02 #3）：通道发现 self_id 后持久化，并提供只读诊断
        services.AddSingleton<ISelfIdRegistry, SelfIdRegistry>();
        services.AddSingleton<SelfIdRecorder>();
        services.AddHostedService(sp => sp.GetRequiredService<SelfIdRecorder>());
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

        RegisterMail(services, options);

        // ── 第三方平台接入页（S3-F / 决策七）：平台总览 + 抖音配置读写 ──
        // 邮件账号的增删改直接用上面那批 mail action，不重复造一套。
        services.AddSingleton<IApiAction, GetPlatformsAction>();
        services.AddSingleton<IApiAction, SaveDouyinAction>();
        services.AddSingleton<IApiAction, PurgeSessionAction>();
        services.AddSingleton<IApiAction, GetDouyinLoginAction>();
        services.AddSingleton<IApiAction, StartDouyinLoginAction>();
        services.AddSingleton<IApiAction, LogoutDouyinAction>();

        // ── M2-E：回复触发模式（FR-11）：every / mention / jev（判断失败降级 mention）──
        services.AddSingleton<IRuntimeSettingsStore, SqliteRuntimeSettingsStore>();
        // decisions 模型（Jev）判断客户端：OpenRouter /api/alpha/decisions
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

        RegisterChannels(services, options);

        RegisterAgent(services, options);

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
