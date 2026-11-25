namespace ZipperAIQQ.Agent.Media;

/// <summary>
/// 一段转写文本，时间是**相对视频开头**的绝对毫秒。
/// </summary>
/// <param name="StartMs">起点（毫秒）。</param>
/// <param name="EndMs">终点（毫秒）。</param>
/// <param name="Text">这段说的话（ASR 原文，不做润色）。</param>
public sealed record VideoTranscriptLine(long StartMs, long EndMs, string Text);
