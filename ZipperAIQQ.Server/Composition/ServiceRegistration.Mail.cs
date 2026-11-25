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
    private static void RegisterMail(IServiceCollection services, ServerOptions options)
    {
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
                // HTML 留底上限按 KB 配（B3）：它跟"喂模型的正文字数"不是一回事
                MaxHtmlChars = Math.Max(1024, options.Mail.RenderMaxBodyHtmlKb * 1024),
            },
            sp.GetRequiredService<ILogger>()));
        // 源**不**注册成 HostedService（实施微调 M12）：由 MailPipeline 在 ExecuteAsync 里
        // StartAsync / StopAsync，与 MessageRouter 启动 NapCatChannel 完全同构 —— 生命周期只有一处。
        // 邮件策略参数进容器：action 与两个服务都直接注入 MailOptions，不引 IOptions<T>。
        services.AddSingleton(options.Mail);

        // 总开关的**运行时**版本（S3-F 补）：客户端点一下就生效，不必重启。
        // 启动值来自 options.Mail.Enabled（YAML），库里写过之后以库为准。
        services.AddSingleton(sp => new MailOptionsService(
            sp.GetRequiredService<IRuntimeSettingsStore>(),
            options.Mail,
            sp.GetRequiredService<ILogger>()));
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
            // 「汇总再经人格润色」是运行时可选项：关掉就直发
            sp.GetRequiredService<MailOptionsService>(),
            // 汇总先交给人格 agent 去说（用户要求），失败才回落直发
            sp.GetRequiredService<IChatAgentPool>(),
            sp.GetRequiredService<ILogger>()));
        services.AddHostedService<MailPipeline>();

        // ── 邮件双通道（B3）：渲染画面 / 原文直读的模型工具 ──
        // 渲染器是**外置进程**（Node + Playwright）：不可用时 CanRender=false，工具不出现，
        // 服务端照常启动（方案 G4 的降级口径）。
        services.AddSingleton(sp => new MailRenderService(
            options.Mail,
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton(sp => new MailImapAccess(
            sp.GetRequiredService<IImapSessionFactory>(),
            sp.GetRequiredService<IMailRepository>(),
            sp.GetRequiredService<ICredentialProtector>(),
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton(sp => new MailReadService(
            sp.GetRequiredService<IMailRepository>(),
            sp.GetRequiredService<MailImapAccess>(),
            sp.GetRequiredService<MailRenderService>(),
            options.Mail,
            sp.GetRequiredService<ILogger>()));
        // Agent 只认窄接口（IMailReader）：它不知道 IMAP 与 Chromium 的存在
        services.AddSingleton<ZipperAIQQ.Agent.Mail.IMailReader>(sp =>
            sp.GetRequiredService<MailReadService>());
        services.AddSingleton<IApiAction, ListMailsAction>();
        services.AddSingleton<IApiAction, GetMailAction>();
        services.AddSingleton<IApiAction, ListMailAccountsAction>();
        services.AddSingleton<IApiAction, SaveMailAccountAction>();
        services.AddSingleton<IApiAction, DeleteMailAccountAction>();
        services.AddSingleton<IApiAction, SyncMailAction>();
        services.AddSingleton<IApiAction, SaveMailAction>();
    }
}
