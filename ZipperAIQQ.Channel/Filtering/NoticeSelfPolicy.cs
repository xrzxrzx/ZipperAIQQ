using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Channel.Filtering;

/// <summary>
/// 通知与「我自己」的关系判定（问题记录 02 #3 / #4）。
/// </summary>
/// <remarks>
/// <para>
/// 只丢**自己触发**的通知（Actor == self），以及**自己被拉入/退出群**这类系统事件。
/// </para>
/// <para>
/// 戳一戳**一律保留**：A 戳 B 也要让 AI 看到（但正文明确标注「不是你」）；
/// 戳自己更要进响应链路。早期版本把 target == self 一律丢弃，
/// 导致「戳 AI 没反应」——已修正。
/// </para>
/// </remarks>
public static class NoticeSelfPolicy
{
    public static bool IsAboutSelf(IncomingNotice notice, string? selfId)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (string.IsNullOrWhiteSpace(selfId))
        {
            return false;
        }

        return string.Equals(notice.Actor?.UserId, selfId, StringComparison.Ordinal)
               || string.Equals(notice.Target?.UserId, selfId, StringComparison.Ordinal);
    }

    /// <summary>是否应在通道/路由层丢弃（自激过滤）。</summary>
    public static bool ShouldDrop(IncomingNotice notice, string? selfId)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (string.IsNullOrWhiteSpace(selfId))
        {
            return false;
        }

        // 自己触发的操作（自己改群名、自己戳别人等）
        if (string.Equals(notice.Actor?.UserId, selfId, StringComparison.Ordinal))
        {
            return true;
        }

        // 自己被拉入 / 退出群：不需要 AI 回应「自己入群」
        return string.Equals(notice.Target?.UserId, selfId, StringComparison.Ordinal)
               && notice.Type is NoticeType.GroupIncrease or NoticeType.GroupDecrease;
    }
}
