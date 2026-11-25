namespace ZipperAIQQ.Agent.Mail;

/// <summary>
/// 一封邮件的「最小身份」：模型据此决定要看哪一封。
/// </summary>
/// <param name="Id">邮件主键（渲染 / 读原文都拿它当定位键）。</param>
/// <param name="Subject">主题。</param>
/// <param name="From">发件人（「名字 &lt;地址&gt;」或只有地址）。</param>
/// <param name="SentAt">邮件自身的时间（<c>Date</c> 头，不是收信时间）。</param>
/// <param name="HasHtml">
/// 有没有 HTML 正文。<see langword="false"/> 时渲染出来就是纯文本版面 ——
/// 画面通道对它没意义，模型该直接用正文摘要回答。
/// </param>
/// <param name="HasUid">
/// 有没有 IMAP UID。旧版本收下的邮件没有这一列，读原文只能失败并说明原因；
/// 提前告诉模型，省得它挑了一封读不了的邮件再撞一次墙。
/// </param>
public sealed record MailBrief(
    long Id,
    string Subject,
    string From,
    DateTimeOffset SentAt,
    bool HasHtml,
    bool HasUid);
