namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 一条出站消息（平台无关）。
/// </summary>
/// <remarks>
/// @ 某人通过 <see cref="MentionSegment"/> 表达，不设单独的 <c>MentionUserIds</c> 字段 ——
/// 否则「@ 出现在文本中间」这种情况就没法表达，且两个字段的先后关系会变得模糊。
/// </remarks>
public sealed record OutgoingMessage
{
    /// <summary>目标会话。</summary>
    public required ChatRef Chat { get; init; }

    /// <summary>内容片段（保序）。</summary>
    public required IReadOnlyList<MessageSegment> Segments { get; init; }

    /// <summary>是否以「引用回复」发出，以及引用哪条。</summary>
    public string? ReplyToMessageId { get; init; }

    /// <summary>拼接后的纯文本（仅文本片段）。</summary>
    public string PlainText =>
        string.Concat(Segments.OfType<TextSegment>().Select(s => s.Text));
}
