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
    private static void RegisterAgent(IServiceCollection services, ServerOptions options)
    {
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
                OwnerLookup = sp.GetRequiredService<ZipperAIQQ.Agent.IOwnerLookup>(),
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
    }
}
