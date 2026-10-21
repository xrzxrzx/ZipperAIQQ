namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 一条入站消息（平台无关）。
/// </summary>
/// <remarks>
/// <para>
/// 继承 <see cref="InboundEvent"/>：与通知共享「会话 + 发生时间 + 事件 ID」（FR-18.1）。
/// </para>
/// <para>
/// ⚠️ 平台消息 ID 用继承来的 <see cref="InboundEvent.EventId"/>，**不再单独定义 <c>MessageId</c>** ——
/// 同一份数据挂两个名字，迟早会有一处忘记赋值。
/// </para>
/// <para>
/// ⚠️ <see cref="InboundEvent.OccurredAt"/> 必须取**平台时间戳**，不要在接收时重新取 <c>UtcNow</c> ——
/// 网络延迟与补发消息场景下两者会差出可观时间（FR-15.1）。
/// </para>
/// </remarks>
public sealed record IncomingMessage : InboundEvent
{
    /// <summary>发送者。</summary>
    public required SenderInfo Sender { get; init; }

    /// <summary>内容片段（保序）。</summary>
    public required IReadOnlyList<MessageSegment> Segments { get; init; }

    /// <summary>被回复的消息 ID（若这是一条引用回复）。</summary>
    public string? ReplyToMessageId { get; init; }

    /// <summary>拼接后的纯文本（仅文本片段；用于检索与规则判定）。</summary>
    public string PlainText =>
        string.Concat(Segments.OfType<TextSegment>().Select(s => s.Text));
}
