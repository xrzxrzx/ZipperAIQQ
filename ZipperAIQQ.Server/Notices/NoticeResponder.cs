using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Feedback;
using ZipperAIQQ.Storage.Notices;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Notices;

/// <summary>
/// 通知处理（FR-18）：**先落库，再按配置决定要不要响应**。
/// </summary>
/// <remarks>
/// <para>
/// 默认**只记录不响应**（18.3 的默认口径）：入群/禁言这类通知如果默认就回，
/// 群里会显得吵闹，而且有些场景（撤回）回一句反而尴尬。
/// </para>
/// <para>
/// 开启后把通知**描述成一条消息**交给 Agent —— 这样回复能带人设，
/// 而不是一段固定模板。
/// </para>
/// <para>
/// **开关按平台读**（决策六 / S3-D）：键是 <c>notice.reply:{platform}:{type}</c>，
/// 平台取<b>通知发生的那个会话</b>（<see cref="InboundEvent.Chat"/> 的 Platform），
/// 不是通道的默认平台 —— 复合通道下"在哪收到的"才决定"该读哪一端的开关"。
/// 读取统一走 <see cref="GetBehaviorAction.ReadNoticeValueAsync"/>：那里管着
/// 「新键优先、仅 qq 回退旧键」的唯一口径，本类**不要**自己拼键。
/// </para>
/// </remarks>
public sealed class NoticeResponder
{
    private static readonly SenderInfo SyntheticSender = new()
    {
        UserId = "system",
        Nickname = "系统",
    };

    private readonly INoticeStore _notices;
    private readonly IRuntimeSettingsStore _settings;
    private readonly IChatAgentPool _agents;
    private readonly IMessageChannel _channel;
    private readonly ServerOptions _options;
    private readonly OwnerFeedbackService _feedback;
    private readonly Identity.ISelfIdRegistry _selfIds;
    private readonly ILogger _logger;

    public NoticeResponder(
        INoticeStore notices,
        IRuntimeSettingsStore settings,
        IChatAgentPool agents,
        IMessageChannel channel,
        ServerOptions options,
        OwnerFeedbackService feedback,
        Identity.ISelfIdRegistry selfIds,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(selfIds);
        ArgumentNullException.ThrowIfNull(logger);

        _notices = notices;
        _settings = settings;
        _agents = agents;
        _channel = channel;
        _options = options;
        _feedback = feedback;
        _selfIds = selfIds;
        _logger = logger;
    }

    /// <summary>落库 + 按开关决定是否让 Agent 响应。</summary>
    public async Task HandleAsync(IncomingNotice notice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        try
        {
            await _notices.AppendAsync(notice, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "通知落库失败（{Type}，会话 {Session}）", notice.Type, notice.Chat.SessionId);
        }

        // 主人反馈（问题记录 03 #4）：独立于「是否在当前会话回应」
        try
        {
            await _feedback.HandleAsync(notice, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "主人反馈处理失败（{Type}）", notice.Type);
        }

        // 请求类（加好友 / 加群）**只记录不响应**：自动通过等于替主人做决定。
        // ⚠️ 只针对 QQ 这两个类型：抖音的 DouyinFriendRequest / DouyinGroupRequest **不进这里** ——
        // 它们在「运行与行为」页有独立开关（默认关 = 同样只记录），开关管的是"要不要说句话"，
        // 不是"要不要替主人通过请求"（本项目没有自动通过能力）。
        if (notice.Type is NoticeType.FriendRequest or NoticeType.GroupRequest)
        {
            _logger.Information("请求已记录（{Raw}）：不自动处理", notice.RawType);
            return;
        }

        // 开关按**通知发生的会话所在平台**读（决策六）：复合通道下不能用通道默认平台
        if (!await IsEnabledAsync(notice.Type, notice.Chat.Platform, ct).ConfigureAwait(false))
        {
            _logger.Information("通知 {Type}（{Raw}）已记录，未开启响应（会话 {Session}）",
                notice.Type, notice.RawType, notice.Chat.SessionId);
            return;
        }

        // 按平台取 selfId（S3-A）：多平台下 QQ 号不能拿来判抖音通知
        var selfId = _channel.SelfIdFor(notice.Chat.Platform)
            ?? _options.SelfIdFor(notice.Chat.Platform)
            ?? _selfIds.Get(notice.Chat.Platform);
        var text = Describe(notice, selfId);

        try
        {
            await _agents.GetOrCreate(notice.Chat).RunAsync(new AgentRunRequest
            {
                Chat = notice.Chat,
                Sender = notice.Actor ?? notice.Target ?? SyntheticSender,
                UserText = text,
                Segments = [new TextSegment(text)],
                Now = notice.OccurredAt,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "通知响应失败（{Type}，会话 {Session}）", notice.Type, notice.Chat.SessionId);
        }
    }

    /// <summary>
    /// 某平台上的某类通知是否开启了响应（**默认关**：键不存在 = 关）。
    /// </summary>
    /// <param name="type">通知类型。</param>
    /// <param name="platform">通知发生的平台（<see cref="ChatRef.Platform"/>）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>
    /// 读键的口径统一交给 <see cref="GetBehaviorAction.ReadNoticeValueAsync"/>：
    /// 新键 <c>notice.reply:{platform}:{type}</c> 优先，**仅 qq** 回退单平台时代的老键
    /// <c>notice.reply:{type}</c>。两处各写一遍回退逻辑迟早漂移（一边漏了回退、
    /// 一边把平台段写错），所以这里只做"值 → 布尔"的翻译。
    /// </remarks>
    public async Task<bool> IsEnabledAsync(
        NoticeType type,
        string platform,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var value = await GetBehaviorAction
            .ReadNoticeValueAsync(_settings, platform, type, ct)
            .ConfigureAwait(false);

        return value is "1" or "true";
    }

    /// <summary>把通知翻译成给模型看的一句话（保留 actor / target 的区分）。</summary>
    public static string Describe(IncomingNotice notice, string? selfId = null) => notice.Type switch
    {
        NoticeType.Poke => string.Equals(notice.Target?.UserId, selfId, StringComparison.Ordinal)
            ? $"[戳一戳] {Who(notice.Actor)} 戳了你"
            : $"[戳一戳] {Who(notice.Actor)} 戳了 {Who(notice.Target)}（不是你，别当成戳自己）",
        // 成员增减事件没有 target_id 时，被操作者就是 user_id（映射层已补；这里再兜一次）
        NoticeType.GroupIncrease => $"[入群] {Who(notice.Target ?? notice.Actor)} 加入了群",
        NoticeType.GroupDecrease => $"[退群] {Who(notice.Target ?? notice.Actor)} 离开了群",
        NoticeType.GroupAdminChanged => $"[管理员变动] {Who(notice.Target)} 的管理员身份发生变化（操作者：{Who(notice.Actor)}）",
        NoticeType.GroupBan => $"[禁言] {Who(notice.Target)} 被 {Who(notice.Actor)} 禁言",
        NoticeType.MessageRecalled => $"[撤回] {Who(notice.Actor)} 撤回了一条消息",
        NoticeType.FriendAdd => $"[好友添加] {Who(notice.Actor ?? notice.Target)} 添加了好友",
        NoticeType.GroupFileUpload => $"[群文件] {Who(notice.Actor)} 上传了文件",
        NoticeType.GroupCardChanged => $"[群名片] {Who(notice.Target ?? notice.Actor)} 修改了群名片",
        NoticeType.GroupNameChanged => $"[群名变更] {Who(notice.Actor)} 修改了群名",
        NoticeType.GroupTitleChanged => $"[群头衔] {Who(notice.Actor)} 修改了群头衔",
        NoticeType.GroupGrayTip => $"[群提示] {Who(notice.Actor)} 触发了一条群提示",
        NoticeType.ProfileLike => $"[资料点赞] {Who(notice.Actor)} 点赞了资料",
        NoticeType.InputStatus => $"[输入状态] {Who(notice.Actor)} 正在输入",
        NoticeType.EssenceChanged => $"[群精华] {Who(notice.Actor)} 调整了精华消息",
        NoticeType.MessageEmojiLike => $"[表情回应] {Who(notice.Actor)} 回应了消息",
        NoticeType.BotOffline => "[离线] 机器人已离线",
        NoticeType.FriendRequest => $"[好友请求] {Who(notice.Actor)} 请求添加好友",
        NoticeType.GroupRequest => $"[加群请求] {Who(notice.Actor)} 请求加入群",

        // 抖音语义（决策六 / S3-E）：措辞不带 QQ 术语（没有"群名片/群精华"这类概念），
        // 只说清"谁做了什么"，让模型自己组织人设化回复。
        NoticeType.DouyinFriendRequest => $"[抖音好友申请] {Who(notice.Actor)} 请求添加好友",
        NoticeType.DouyinGroupRequest => $"[抖音入群申请] {Who(notice.Actor)} 申请加入群聊",
        NoticeType.DouyinMemberChanged => $"[抖音成员变化] {Who(notice.Target ?? notice.Actor)} 的成员状态发生变化（操作者：{Who(notice.Actor)}）",
        NoticeType.DouyinSessionSynced => $"[抖音会话同步] 会话信息已同步（涉及：{Who(notice.Actor ?? notice.Target)}）",

        _ => $"[通知] {notice.RawType}",
    };

    private static string Who(SenderInfo? who) =>
        who is null ? "某人" : $"{who.DisplayName}（{who.UserId}）";
}
