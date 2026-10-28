namespace ZipperAIQQ.Server.Mail;

/// <summary>邮件文本的公共拼法。</summary>
internal static class MailText
{
    /// <summary>
    /// 发件人显示：有名字时「名字 &lt;地址&gt;」，否则只给地址。
    /// </summary>
    /// <remarks>
    /// 摘要与汇总两条路径都要给模型看发件人，以前各写了一份 —— 改一边漏一边
    /// 就会出现「摘要里认得出人、汇总里只剩地址」。
    /// </remarks>
    public static string DescribeSender(string? fromName, string fromAddress) =>
        string.IsNullOrWhiteSpace(fromName) ? fromAddress : $"{fromName} <{fromAddress}>";
}
