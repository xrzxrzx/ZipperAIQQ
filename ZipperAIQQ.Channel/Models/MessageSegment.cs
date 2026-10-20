namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 消息内容片段。一条消息由若干片段组成，顺序即呈现顺序。
/// </summary>
/// <remarks>
/// 用「片段列表」而不是「一段纯文本 + 若干附加字段」，是为了让
/// <c>[图片][@某人] 你看这个</c> 这类混排消息保序，且新增类型时不必改外层结构。
/// </remarks>
public abstract record MessageSegment;

/// <summary>纯文本。</summary>
public sealed record TextSegment(string Text) : MessageSegment;

/// <summary>图片。M1 只透传，不做图像理解（见需求 D-4 / U13）。</summary>
public sealed record ImageSegment(string? Url, string? FileId) : MessageSegment;

/// <summary>@ 某人。</summary>
public sealed record MentionSegment(string UserId) : MessageSegment;

/// <summary>对某条消息的引用回复。</summary>
public sealed record ReplySegment(string MessageId) : MessageSegment;
