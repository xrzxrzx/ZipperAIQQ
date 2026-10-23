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
[JsonDerivedType(typeof(FaceSegment), "face")]
public abstract record MessageSegment;

/// <summary>纯文本。</summary>
public sealed record TextSegment(string Text) : MessageSegment;

/// <summary>图片。M1 只透传，不做图像理解（见需求 D-4 / U13）。</summary>
public sealed record ImageSegment(string? Url, string? FileId) : MessageSegment;

/// <summary>@ 某人。</summary>
public sealed record MentionSegment(string UserId) : MessageSegment;

/// <summary>对某条消息的引用回复。</summary>
public sealed record ReplySegment(string MessageId) : MessageSegment;

/// <summary>QQ 系统表情（face）。保留原始 id，名称由上层按平台表情表渲染。</summary>
public sealed record FaceSegment(string Id) : MessageSegment;

/// <summary>
/// QQ **表情包**（<c>mface</c>）—— 和系统表情（<see cref="FaceSegment"/>）不是一回事。
/// </summary>
/// <param name="EmojiId">表情 id（发送时必需）。</param>
/// <param name="Url">静态图 url（可能为空）。</param>
/// <param name="Summary">摘要文字（"笑哭""猫猫头"这类，平台给的）。</param>
/// <remarks>
/// 之前**只解析不出站**：用户发表情包 AI 看得见，但没能力发回同一张，
/// 只能文字描述"蓝蓝的一坨"。这个类型就是为了补上出站通道。
/// </remarks>
public sealed record MfaceSegment(string? EmojiId, string? Url, string? Summary) : MessageSegment;
