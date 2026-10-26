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
    MailSummaryRow? Summary);

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
