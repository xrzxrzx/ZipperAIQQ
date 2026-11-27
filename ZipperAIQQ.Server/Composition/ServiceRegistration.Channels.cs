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
using ZipperAIQQ.Channel.Reliability;
using ZipperAIQQ.Channel.Apis;
using ZipperAIQQ.Server.Ai;
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
    private static void RegisterChannels(IServiceCollection services, ServerOptions options)
    {
        // ── 接收过滤（S3-C：按平台分桶）：启动种子只喂 qq（环境变量 / YAML 是单平台时代的写法），
        //    其余平台的名单由 Program.cs 从库里按平台灌入 ──
        services.AddSingleton<IChatFilter>(_ =>
            new ChatFilter(BuildFilterOptions(options), Access.AccessKeys.DefaultPlatform));

        // ── 通道（内层 NapCat + 外层节流）──
        services.AddSingleton<IClientTransport>(sp =>
            new ClientTransport(sp.GetRequiredService<ILogger>()));
        // 内层通道单独注册：IContactDirectory（好友/群/群成员查询）需要它，
        // 而消息发送要过外层节流 —— 两者拿的不是同一个对象。
        services.AddSingleton<NapCatChannel>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger>();

            // B00001：单条语音的墙上时间上限与全局 API 超时分开 ——
            // 复用 30 秒会让「一条转不出来的语音」把事件接收卡 30~60 秒
            var speech = new SpeechOptions { Enabled = options.SttEnabled, TimeoutMs = options.SttTimeoutMs };

            var channel = new NapCatChannel(
                sp.GetRequiredService<IClientTransport>(),
                sp.GetRequiredService<IChatFilter>(),
                new NapCatOptions
                {
                    WebSocketUrl = options.NapCatWebSocketUrl,
                    AccessToken = options.NapCatAccessToken,
                },
                logger,
                speech);

            // 语音转写链（B00001，用户 2026-11-28 定）：**NapCat 自转优先，转不出走 DS ASR**。
            // 顺序不能反：自转免费且不用下载音频；ASR 要取音频 + 按秒计费。
            // ASR 客户端在 Server 侧（要 key），通道只拿到一个 ISpeechTranscriber —— 依赖方向不变。
            var media = sp.GetService<ZipperAIQQ.Agent.Media.IDeepSeekMediaClient>();
            if (media is not null)
            {
                // silk 解码走外置 Node（silk-wasm 已 vendored 进仓库，部署时不用 npm）
                var asr = new AsrSpeechTranscriber(
                    options.Ai,
                    media,
                    channel,
                    new NodeSilkDecoder(options.Ai, logger),
                    speech,
                    logger);

                channel.UseSpeechTranscriber(
                    new FallbackSpeechTranscriber(channel.SpeechTranscriber, asr, logger));
            }

            return channel;
        });

        services.AddSingleton<IContactDirectory>(sp => sp.GetRequiredService<NapCatChannel>());

        // 通道健康与看门狗：平台只实现 IRecoverableChannel，循环/退避/熔断共用一份
        services.AddSingleton(sp => sp.GetRequiredService<Configuration.ServerOptions>().ChannelReliability);
        services.AddSingleton(sp => sp.GetRequiredService<Configuration.ServerOptions>().Alerts);
        services.AddSingleton<ChannelHealthRegistry>();

        // 告警：投递记录 + 主人跨平台账号镜像 + 投递服务（D 批）
        services.AddSingleton<Storage.Alerts.IAlertStore, Storage.Alerts.SqliteAlertStore>();
        services.AddSingleton<Storage.Owners.IOwnerAccountStore, Storage.Owners.SqliteOwnerAccountStore>();
        services.AddSingleton<Alerts.OwnerAlertService>();
        services.AddSingleton<Alerts.IAlertSink>(sp => sp.GetRequiredService<Alerts.OwnerAlertService>());

        // 余额采样 + 额度闸门（E 批）
        services.AddSingleton<Storage.Billing.IBalanceStore, Storage.Billing.SqliteBalanceStore>();
        services.AddSingleton<Billing.QuotaOptionsService>(sp => new(
            sp.GetRequiredService<Storage.Settings.IRuntimeSettingsStore>(),
            sp.GetRequiredService<Configuration.ServerOptions>().Quota,
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<Billing.IQuotaOptionsProvider>(sp => sp.GetRequiredService<Billing.QuotaOptionsService>());
        services.AddSingleton<Api.IApiAction, Api.Handlers.GetQuotaAction>();
        services.AddSingleton<Api.IApiAction, Api.Handlers.SetQuotaAction>();
        services.AddSingleton(sp => sp.GetRequiredService<Configuration.ServerOptions>().Billing);
        services.AddSingleton<Billing.ISpendReader, Billing.SpendReader>();
        services.AddHostedService<Billing.BalanceMonitor>();
        services.AddHttpClient(nameof(Billing.BalanceMonitor));
        services.AddSingleton<Api.IApiAction, Api.Handlers.GetAlertsAction>();
        services.AddSingleton<IRecoverableChannel>(sp => sp.GetRequiredService<NapCatChannel>());
        services.AddSingleton<ZipperAIQQ.Agent.IChannelHealthLookup, ChannelHealthLookup>();
        services.AddHostedService<ChannelSupervisor>();

        // sidecar 卡死看护（进程活着但 /ws/channel 迟迟连不回来）
        services.AddHostedService<SidecarHealthMonitor>();

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
            // 会话过滤与 QQ 同一套（access.douyin.whitelist / blacklist，入口在「人员与名单」）
            sp.GetRequiredService<IChatFilter>(),
            sp.GetRequiredService<ILogger>()));

        // 登录相关 action 只依赖窄接口，不直接注入具体通道（换实现不用改 action）
        services.AddSingleton<Channels.IChannelLogin>(sp =>
            sp.GetRequiredService<Channels.DouyinChannel>());
        services.AddSingleton<IRecoverableChannel>(sp => sp.GetRequiredService<Channels.DouyinChannel>());

        // 视频媒体（B4）：取详情 / 抽帧 / 抽音的唯一入口。由通道实现（连接与 requestId 配对在它手里），
        // 消费者只认这个窄接口 —— 单测可以塞假实现，不必起 WS。
        services.AddSingleton<Media.IDouyinMediaSource>(sp =>
            sp.GetRequiredService<Channels.DouyinChannel>());

        // 视频理解（B4）：抽帧 + 拼音频 + 调 ASR。开关与上限来自 douyin.video* 配置。
        services.AddSingleton(sp => new Media.VideoReadService(
            sp.GetRequiredService<Media.IDouyinMediaSource>(),
            sp.GetRequiredService<ZipperAIQQ.Agent.Media.IDeepSeekMediaClient>(),
            sp.GetRequiredService<Configuration.ServerOptions>().Douyin.Video,
            sp.GetRequiredService<ILogger>()));
        services.AddSingleton<ZipperAIQQ.Agent.Media.IVideoReader>(sp =>
            sp.GetRequiredService<Media.VideoReadService>());

        // 评论区（B5）：同样是窄接口 + 由通道实现（连接与 requestId 配对在通道手里）。
        // 读评论要知道「抖音这边主人叫什么」才能判 `@主人`，所以把 IOwnerLookup 一起给它。
        services.AddSingleton<Douyin.IDouyinCommentSource>(sp =>
            sp.GetRequiredService<Channels.DouyinChannel>());

        // 写闸门（G3）：配额与留痕同一张表（DouyinWrites，schema v21）
        services.AddSingleton<Storage.Douyin.IDouyinWriteStore, Storage.Douyin.SqliteDouyinWriteStore>();
        services.AddSingleton<Douyin.IWriteGate, Douyin.WriteGate>();

        services.AddSingleton(sp => new Douyin.DouyinCommentService(
            sp.GetRequiredService<Douyin.IDouyinCommentSource>(),
            sp.GetRequiredService<Configuration.ServerOptions>().Douyin.Comment,
            sp.GetRequiredService<Douyin.IWriteGate>(),
            sp.GetRequiredService<ILogger>(),
            sp.GetService<ZipperAIQQ.Agent.IOwnerLookup>()));
        services.AddSingleton<ZipperAIQQ.Agent.Comments.ICommentReader>(sp =>
            sp.GetRequiredService<Douyin.DouyinCommentService>());
        services.AddSingleton<ZipperAIQQ.Agent.Comments.ICommentReplier>(sp =>
            sp.GetRequiredService<Douyin.DouyinCommentService>());

        // 联系人目录按平台注册（决策四第 6 条）：配置页 / 主人放行范围 / 平台专用工具都只认平台名。
        // ⚠️ 抖音**无条件**注册（S3-F 补）：之前按启动配置决定加不加，结果"客户端打开开关"不生效 ——
        //    注册表/复合通道是启动时定死的，运行时改不了。现在改成通道常驻、由快照决定收不收发不发。
        services.AddSingleton<IContactDirectoryRegistry>(sp =>
        {
            var map = new Dictionary<string, IContactDirectory>(StringComparer.OrdinalIgnoreCase)
            {
                [Access.AccessKeys.DefaultPlatform] = sp.GetRequiredService<IContactDirectory>(),
                [Channels.DouyinChannel.PlatformName] = sp.GetRequiredService<Channels.DouyinChannel>(),
            };

            return new ContactDirectoryRegistry(map);
        });

        // Agent 层按平台取目录（抖音专用工具用）：同一个注册表，只是接口更窄
        services.AddSingleton<IPlatformContacts>(sp =>
            sp.GetRequiredService<IContactDirectoryRegistry>());

        // 会话显示名（#2）：好友昵称 / 群名，带 5 分钟缓存
        services.AddSingleton<ISessionNameResolver, SessionNameResolver>();

        // 主人查询（Agent 用）：复用 AccessSettings 的每平台口径 + 旧键回退
        services.AddSingleton<ZipperAIQQ.Agent.IOwnerLookup, AccessOwnerLookup>();

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

                // 抖音是第二个 IMessageChannel：**常驻**注册（S3-F 补）。
                // 启不启用由 DouyinOptionsService 快照决定（入站丢弃 / 出站抛「已停用」），
                // 这样客户端改开关是**立即生效**的，不必重启服务。
                sp.GetRequiredService<Channels.DouyinChannel>(),
            };

            var composite = new Channels.CompositeMessageChannel(
                channels, sp.GetRequiredService<ILogger>());

            // 所有出站消息都过这里：全局最短间隔 + 连发冷却，跨会话共享
            var throttled = new ThrottledMessageChannel(
                composite,
                options.SendIntervalMs,
                options.MaxConsecutive,
                options.CooldownMs);

            // 最外层统计真实发送结果 → 喂给健康状态机（熔断后快速失败）
            return new Channels.HealthTrackingMessageChannel(
                throttled,
                sp.GetRequiredService<ChannelHealthRegistry>(),
                sp.GetRequiredService<ILogger>());
        });
    }
}

