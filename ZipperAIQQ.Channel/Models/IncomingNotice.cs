namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 平台通知事件（戳一戳 / 入群退群 / 管理员变动 / 禁言 / 撤回 …）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Actor"/> 与 <see cref="Target"/> 要**分开记**（FR-18.4）：
/// 戳一戳天然是「A 戳 B」，只记「发生了一次戳一戳」的话，AI 分不清该对谁说话 ——
/// 「你怎么又戳我」和「你戳他干嘛」是两种完全不同的反应。
/// </para>
/// <para>
/// 某些通知没有 actor（如入群事件里新成员是 target、操作者可能是 null）。
/// 因此两者都可空，**不要用非空约束硬套**。
/// </para>
/// </remarks>
public sealed record IncomingNotice : InboundEvent
{
    /// <summary>映射后的通知类型。</summary>
    public required NoticeType Type { get; init; }

    /// <summary>
    /// 平台原始通知类型字符串（如 <c>notice.notice_type</c>）。
    /// </summary>
    /// <remarks>
    /// 保留原文是因为：① <see cref="NoticeType.Unknown"/> 时要能看出原本是什么；
    /// ② 平台新增类型时不必改枚举就能先记录。
    /// </remarks>
    public required string RawType { get; init; }

    /// <summary>操作者（谁触发的）。可空。</summary>
    public SenderInfo? Actor { get; init; }

    /// <summary>被操作者（戳的谁 / 踢的谁 / 禁言的是谁）。可空。</summary>
    public SenderInfo? Target { get; init; }

    /// <summary>相关消息 ID（如撤回事件里被撤回的那条）。</summary>
    public string? RelatedMessageId { get; init; }

    /// <summary>类型相关的附加字段（时长、操作结果等）。</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}
