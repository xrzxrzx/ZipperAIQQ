namespace ZipperAIQQ.Agent.Comments;

/// <summary>
/// 一次回评论的结果（B5）。
/// </summary>
/// <param name="Ok">成没成。</param>
/// <param name="Error">失败原因（人话）。</param>
/// <param name="Denied">是不是**被写闸门拒的**（不是底座失败 —— 两者的处置完全不同）。</param>
/// <param name="DryRun">是不是干跑（只填入、没提交）。</param>
/// <param name="MatchedBy">靠什么定位到的（<c>id</c> / <c>text</c> / <c>author+text</c>）。</param>
/// <param name="Readback">提交后是否在页面上回读到自己那条。</param>
/// <param name="Note">补充说明（照实转述给用户）。</param>
public sealed record CommentReplyOutcome(
    bool Ok,
    string? Error,
    bool Denied,
    bool DryRun,
    string? MatchedBy,
    bool Readback,
    string? Note)
{
    /// <summary>底座 / 页面层面的失败。</summary>
    /// <param name="error">人话原因。</param>
    public static CommentReplyOutcome Failed(string error) =>
        new(false, error, false, false, null, false, null);

    /// <summary>被写闸门拒绝（配额 / 间隔 / 同人一次 / 开关没开）。</summary>
    /// <param name="reason">人话原因。</param>
    public static CommentReplyOutcome Refused(string reason) =>
        new(false, reason, true, false, null, false, null);
}
