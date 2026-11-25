using MailKit;
using MimeKit;
using ZipperAIQQ.Mail;

namespace ZipperAIQQ.Tests.Mail;

/// <summary>
/// 「正文在哪」的挑选规则：递归穿多层、rfc822 嵌套、**排除附件**。
/// </summary>
/// <remarks>
/// 这一条是 B3 独立审查的 P1-2：带 <c>报告.txt</c> / <c>index.html</c> 附件的邮件，
/// 附件同样是 <see cref="BodyPartText"/>；不排除就会出现「读原文读到了附件内容」、
/// 「渲染画面渲染的是附件」。所以这里直接把夹具摆在同一个 multipart 里。
/// </remarks>
public sealed class MailBodyPartsTests
{
    [Fact]
    public void TextParts_SkipsAttachments()
    {
        var body = Text("text/plain", isAttachment: false);
        var attachment = Text("text/plain", isAttachment: true);
        var tree = Multipart(body, attachment);

        var parts = MailBodyParts.TextParts(tree).ToList();

        Assert.Same(body, Assert.Single(parts));
    }

    [Fact]
    public void FindHtml_PicksBodyHtml_NotHtmlAttachment()
    {
        var htmlAttachment = Text("text/html", isAttachment: true);
        var htmlBody = Text("text/html", isAttachment: false);
        var tree = Multipart(htmlAttachment, htmlBody);

        var found = MailBodyParts.FindHtml(tree);

        Assert.Same(htmlBody, found);
    }

    [Fact]
    public void TextParts_WalksNestedMultiparts_AndRfc822()
    {
        var plain = Text("text/plain", isAttachment: false);
        var inner = Text("text/html", isAttachment: false);
        // rfc822 嵌套（转发成附件的那种）：正文在内层
        var forwarded = new BodyPartMessage(ContentType.Parse("message/rfc822"), "2") { Body = Multipart(inner) };
        var root = Multipart(Multipart(plain), forwarded);

        var parts = MailBodyParts.TextParts(root).ToList();

        Assert.Equal([plain, inner], parts);
    }

    [Fact]
    public void TextParts_Null_IsEmpty()
    {
        Assert.Empty(MailBodyParts.TextParts(null));
        Assert.Null(MailBodyParts.FindHtml(null));
    }

    private static BodyPartText Text(string mime, bool isAttachment) =>
        new(ContentType.Parse(mime), "1")
        {
            // MailKit 的判定口径就是「Content-Disposition 是 attachment」
            ContentDisposition = new ContentDisposition(
                isAttachment ? ContentDisposition.Attachment : ContentDisposition.Inline),
        };

    private static BodyPartMultipart Multipart(params BodyPart[] children) =>
        new(ContentType.Parse("multipart/mixed"), "1", [.. children]);
}
