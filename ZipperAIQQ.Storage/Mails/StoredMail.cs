namespace ZipperAIQQ.Storage.Mails;

/// <summary>
/// 一封已落库的邮件（读模型）。
/// </summary>
/// <param name="Id">主键。</param>
/// <param name="MessageId">RFC <c>Message-Id</c>；平台没给时为 <c>null</c>（此时不做去重）。</param>
/// <param name="Summary">摘要行；还没摘要时为 <c>null</c>。</param>
public sealed record StoredMail(
    long Id,
    string AccountId,
    string? MessageId,
    string Subject,
    string FromAddress,
    string? FromName,
    string BodyText,
    bool HasAttachments,
    DateTimeOffset SentAt,
    DateTimeOffset ReceivedAt,
    MailSummaryRow? Summary)
{
    /// <summary>
    /// HTML 正文原文（已按 <c>mail.render.maxBodyHtmlKb</c> 截断）；不是 HTML 邮件或旧行为 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么要存：渲染画面必须在「收信那一刻」留底 —— 163 会清过期件，按需渲染时再去取，
    /// 邮件可能已经不在服务器上了。
    /// </para>
    /// <para>
    /// **默认摘要路径不读它**（设计 §6.1 的口径不变）：只有 <c>mail_render</c> 显式调用才用。
    /// 用 init 属性而不是构造参数：位置记录的形参一改，所有 <c>new StoredMail(...)</c> 都得跟着改。
    /// </para>
    /// </remarks>
    public string? BodyHtml { get; init; }

    /// <summary>IMAP UID（按需读原文 / 重取 HTML 的定位键）；旧行为 <c>null</c>。</summary>
    public long? Uid { get; init; }
}

/// <summary>
/// 邮件的摘要状态（一封信一行）。
/// </summary>
/// <param name="Status"><c>ok</c> 或 <c>failed</c>。</param>
/// <param name="Error">失败原因；成功时为 <c>null</c>。**不静默丢信**：失败也留行。</param>
/// <param name="ForwardedAt">已汇总转发的时间；<c>null</c> = 还没转发。</param>
public sealed record MailSummaryRow(
    string Text,
    bool NeedsAction,
    string Status,
    string? Error,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ForwardedAt)
{
    /// <summary>摘要是否成功（失败时 UI 显示原因，邮件正文仍可读）。</summary>
    public bool IsOk => string.Equals(Status, "ok", StringComparison.Ordinal);
}
