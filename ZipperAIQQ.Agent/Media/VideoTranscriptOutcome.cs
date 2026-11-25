namespace ZipperAIQQ.Agent.Media;

/// <summary>
/// 转写结果。
/// </summary>
/// <param name="Lines">逐段文本（时间是相对视频开头的绝对毫秒）。</param>
/// <param name="Text">全部文本拼起来（段间换行）。</param>
/// <param name="DurationMs">**被转写的那一段**有多长（毫秒）；不是整条视频。</param>
/// <param name="Note">补充说明（截断 / 只转写了前 N 分钟 / 无人声）；没有时为空串。</param>
/// <param name="Error">失败原因；成功时为 <see langword="null"/>。</param>
public sealed record VideoTranscriptOutcome(
    IReadOnlyList<VideoTranscriptLine> Lines,
    string Text,
    long DurationMs,
    string Note,
    string? Error)
{
    /// <summary>构造一个失败结果。</summary>
    public static VideoTranscriptOutcome Failed(string error) => new([], string.Empty, 0, string.Empty, error);
}
