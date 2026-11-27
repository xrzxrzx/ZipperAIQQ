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
[JsonDerivedType(typeof(RecordSegment), "record")]
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
/// 语音消息（<c>record</c>）。**转写在通道层完成**（FR-1.5），上层只读 <see cref="Text"/>。
/// </summary>
/// <param name="FileId">平台文件标识（取音频 / 转写都要它）。</param>
/// <param name="Url">音频地址（平台可能不给）。</param>
/// <param name="Text">转写结果；<c>null</c> = 转写不可用或失败。</param>
/// <param name="Transcriber">转写来源（如 <c>napcat</c>），便于排查。</param>
/// <remarks>
/// 为什么保留多态段而不是直接拼进 PlainText：转写可能失败，
/// 上层需要能区分「这是一条语音但没转出来」和「这只是一段文字」。
/// </remarks>
public sealed record RecordSegment(
    string? FileId,
    string? Url,
    string? Text,
    string? Transcriber) : MessageSegment
{
    /// <summary>
    /// 平台**本地文件路径**（OneBot 的 <c>record.data.path</c>）。
    /// </summary>
    /// <remarks>
    /// B00001 的回退路径要用它：NapCat 的 <c>fetch_ptt_text</c> 在某些环境里转不出来
    /// （QQ 侧 ASR 卡死），而 OneBot 事件里**已经给了 amr 的本地绝对路径** ——
    /// 直接读这个文件比再走一次 <c>get_record</c> 更稳（少一次往返还少一条过期 URL）。
    /// 与机器相关（NapCat 装在哪就在哪），所以只在同机部署时可用，读不到就退回 URL。
    /// </remarks>
    public string? FilePath { get; init; }

    /// <summary>
    /// 转写**尝试过但没成功**（平台无响应 / QQ 侧转不出文字）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// B00001：<see cref="Text"/> 为 <c>null</c> 有两种完全不同的含义 ——
    /// 「没试」（转写关闭 / 熔断中）与「试了但没拿到」。两者对模型的下一步不一样：
    /// 后者应该追问「语音没听清，再说一次？」，前者只是「我看到你发了条语音」。
    /// </para>
    /// <para>
    /// 用 <c>init</c> 属性而不是加构造参数：位置参数会让所有既有构造点跟着改，
    /// 而这条信息只在转写失败时才用得上。
    /// </para>
    /// </remarks>
    public bool TranscriptionFailed { get; init; }
}

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
