using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Channel.Filtering;

/// <summary>
/// 通知与「我自己」的关系判定（问题记录 02 #3 / #4）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsAboutSelf"/>：自己触发、或自己是被操作者 → 丢弃（自激）。
/// </para>
/// <para>
/// <see cref="ShouldRespond"/>：只对**被戳的是自己**的戳一戳响应；
/// A 戳 B（两个都不是机器人）仅落库，避免模型把别人的互动当成"冲我来的"。
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

    public static bool ShouldRespond(IncomingNotice notice, string? selfId)
    {
        ArgumentNullException.ThrowIfNull(notice);

        // 戳一戳：只有被戳的是自己才响应；自己戳别人也丢掉。
        if (notice.Type == NoticeType.Poke)
        {
            return !string.IsNullOrWhiteSpace(selfId)
                   && string.Equals(notice.Target?.UserId, selfId, StringComparison.Ordinal)
                   && !string.Equals(notice.Actor?.UserId, selfId, StringComparison.Ordinal);
        }

        // 其余通知由「是否开启响应」决定（这里不做额外限制）。
        return true;
    }
}
