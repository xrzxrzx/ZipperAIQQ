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
            // 会话过滤与 QQ 同一套（access.douyin.whitelist / blacklist，入口在「人员与名单」）
            sp.GetRequiredService<IChatFilter>(),
            sp.GetRequiredService<ILogger>()));

        // 登录相关 action 只依赖窄接口，不直接注入具体通道（换实现不用改 action）
        services.AddSingleton<Channels.IChannelLogin>(sp =>
            sp.GetRequiredService<Channels.DouyinChannel>());

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
            return new ThrottledMessageChannel(
                composite,
                options.SendIntervalMs,
                options.MaxConsecutive,
                options.CooldownMs);
        });
    }
}
