namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 一封入站邮件（平台无关）。
/// </summary>
/// <remarks>
/// <para>
/// 继承 <see cref="InboundEvent"/> 复用「会话 + 发生时间 + 事件 ID」，
/// 但**不新增 <c>ChatType</c> 值**：邮件不是对话，塞进 <see cref="ChatType"/> 会牵动
/// <c>Messages</c> 表与对话链路的整数编码兼容（邮件接入设计 §4.4）。
/// </para>
/// <para>
/// <see cref="InboundEvent.EventId"/> = RFC <c>Message-Id</c>（去重键）；
/// <see cref="InboundEvent.OccurredAt"/> = 邮件 <c>Date</c> 头（**不取 UtcNow**）。
/// </para>
/// <para>
/// <see cref="InboundEvent.Chat"/> 只为满足统一事件形状而填（<c>mail</c> + 账号/邮件 ID），
/// **不参与对话路由**，也不会进 <c>Messages</c> 表。
/// </para>
/// </remarks>
public sealed record IncomingMail : InboundEvent
{
    /// <summary>收信账号 ID（<c>MailAccounts.Id</c>）。</summary>
    public required string AccountId { get; init; }

    /// <summary>主题。</summary>
    public required string Subject { get; init; }

    /// <summary>发件人地址。</summary>
    public required string FromAddress { get; init; }

    /// <summary>发件人显示名（可能为空）。</summary>
    public string? FromName { get; init; }

    /// <summary>纯文本正文（HTML 已由实现层转文本，并按配置截断）。</summary>
    public required string BodyText { get; init; }

    /// <summary>
    /// HTML 正文原文（按配置截断）；不是 HTML 邮件时为 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 存它是为了「按需渲染画面」（<c>mail_render</c>）：服务器上的原始邮件会被清理，
    /// 收信那一刻不留底，以后就渲染不出来。**摘要路径不读它**（设计 §6.1 口径不变）。
    /// </remarks>
    public string? BodyHtml { get; init; }

    /// <summary>IMAP UID（按需读原文的定位键）；非 IMAP 来源为 0。</summary>
    public long Uid { get; init; }

    /// <summary>是否带附件（本期**不解析附件内容**）。</summary>
    public bool HasAttachments { get; init; }
}
