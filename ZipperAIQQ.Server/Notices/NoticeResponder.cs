using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Channel.Models;
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
/// </remarks>
public sealed class NoticeResponder
{
    private const string KeyPrefix = "notice.reply:";

    private static readonly SenderInfo SyntheticSender = new()
    {
        UserId = "system",
        Nickname = "系统",
    };

    private readonly INoticeStore _notices;
    private readonly IRuntimeSettingsStore _settings;
    private readonly IChatAgentPool _agents;
    private readonly ILogger _logger;

    public NoticeResponder(
        INoticeStore notices,
        IRuntimeSettingsStore settings,
        IChatAgentPool agents,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(logger);

        _notices = notices;
        _settings = settings;
        _agents = agents;
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

        if (!await IsEnabledAsync(notice.Type, ct).ConfigureAwait(false))
        {
            _logger.Information("通知 {Type}（{Raw}）已记录，未开启响应（会话 {Session}）",
                notice.Type, notice.RawType, notice.Chat.SessionId);
            return;
        }

        var text = Describe(notice);

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

    /// <summary>某类通知是否开启了响应（默认关）。</summary>
    public async Task<bool> IsEnabledAsync(NoticeType type, CancellationToken ct = default)
    {
        var value = await _settings.GetAsync(KeyPrefix + type, ct).ConfigureAwait(false);
        return value is "1" or "true";
    }

    /// <summary>把通知翻译成给模型看的一句话（保留 actor / target 的区分）。</summary>
    public static string Describe(IncomingNotice notice) => notice.Type switch
    {
        NoticeType.Poke => $"[戳一戳] {Who(notice.Actor)} 戳了 {Who(notice.Target)}",
        NoticeType.GroupIncrease => $"[入群] {Who(notice.Target)} 加入了群",
        NoticeType.GroupDecrease => $"[退群] {Who(notice.Target)} 离开了群",
        NoticeType.GroupAdminChanged => $"[管理员变动] {Who(notice.Target)} 的管理员身份发生变化（操作者：{Who(notice.Actor)}）",
        NoticeType.GroupBan => $"[禁言] {Who(notice.Target)} 被 {Who(notice.Actor)} 禁言",
        NoticeType.MessageRecalled => $"[撤回] {Who(notice.Actor)} 撤回了一条消息",
        _ => $"[通知] {notice.RawType}",
    };

    private static string Who(SenderInfo? who) =>
        who is null ? "某人" : $"{who.DisplayName}（{who.UserId}）";
}
