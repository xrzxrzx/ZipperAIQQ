namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 一条入站消息（平台无关）。
/// </summary>
/// <remarks>
/// <see cref="SentAt"/> 必须取**平台时间戳**，不要在接收时重新取 <c>UtcNow</c> ——
/// 两者在网络延迟或补发消息场景下会差出可观的时间（FR-15.1）。
/// </remarks>
public sealed record IncomingMessage
{
    /// <summary>会话。</summary>
    public required ChatRef Chat { get; init; }

    /// <summary>发送者。</summary>
    public required SenderInfo Sender { get; init; }

    /// <summary>内容片段（保序）。</summary>
    public required IReadOnlyList<MessageSegment> Segments { get; init; }

    /// <summary>平台给出的发送时间。</summary>
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>平台消息 ID（用于去重与引用回复）。</summary>
    public string? MessageId { get; init; }

    /// <summary>被回复的消息 ID（若这是一条引用回复）。</summary>
    public string? ReplyToMessageId { get; init; }

    /// <summary>拼接后的纯文本（仅文本片段；用于检索与规则判定）。</summary>
    public string PlainText =>
        string.Concat(Segments.OfType<TextSegment>().Select(s => s.Text));
}
