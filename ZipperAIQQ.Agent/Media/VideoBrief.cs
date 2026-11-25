namespace ZipperAIQQ.Agent.Media;

/// <summary>
/// 一条视频的「简介」：模型先确认「说的是哪条」，再决定抽帧还是转写。
/// </summary>
/// <param name="ItemId">作品 id（纯数字）。</param>
/// <param name="Title">作品文案（可能为空串）。</param>
/// <param name="DurationMs">时长（毫秒）；<c>0</c> = 平台没给（此时不能做「等分 N 张」）。</param>
public sealed record VideoBrief(string ItemId, string Title, long DurationMs);
