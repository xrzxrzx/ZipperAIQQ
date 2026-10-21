namespace ZipperAIQQ.Channel.Models;

using System.Text.Json.Serialization;

/// <summary>
/// 消息内容片段。一条消息由若干片段组成，顺序即呈现顺序。
/// </summary>
/// <remarks>
/// <para>
/// 用「片段列表」而不是「一段纯文本 + 若干附加字段」，是为了让
/// <c>[图片][@某人] 你看这个</c> 这类混排消息保序，且新增类型时不必改外层结构。
/// </para>
/// <para>
/// 多态序列化：写入数据库时片段要整体落 JSON，因此这里声明了派生类型映射。
/// <c>$type</c> 是**存储格式的一部分**，改名会导致历史数据读不出来。
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TextSegment), "text")]
[JsonDerivedType(typeof(ImageSegment), "image")]
[JsonDerivedType(typeof(MentionSegment), "mention")]
[JsonDerivedType(typeof(ReplySegment), "reply")]
public abstract record MessageSegment;

/// <summary>纯文本。</summary>
public sealed record TextSegment(string Text) : MessageSegment;

/// <summary>图片。M1 只透传，不做图像理解（见需求 D-4 / U13）。</summary>
public sealed record ImageSegment(string? Url, string? FileId) : MessageSegment;

/// <summary>@ 某人。</summary>
public sealed record MentionSegment(string UserId) : MessageSegment;

/// <summary>对某条消息的引用回复。</summary>
public sealed record ReplySegment(string MessageId) : MessageSegment;
