using MailKit;

namespace ZipperAIQQ.Mail;

/// <summary>
/// 「一封邮件的正文在哪里」—— 在 MIME 结构树里挑出**正文**文本部分。
/// </summary>
/// <remarks>
/// <para>
/// 单独抽出来是因为它有两个容易写错的判断，而两处都要用（取 HTML / 读原文）：
/// ① 递归要穿过 <see cref="BodyPartMultipart"/> 与 <see cref="BodyPartMessage"/>（rfc822 嵌套转发）；
/// ② **必须排除 <c>Content-Disposition: attachment</c>** —— 带 <c>报告.txt</c> / <c>index.html</c>
/// 附件的邮件，附件同样是 <see cref="BodyPartText"/>：不排除的话，「读原文」会把附件内容
/// 当成正文拼进来，「渲染画面」甚至可能渲染附件而不是正文。
/// </para>
/// <para>
/// 公开是为了可测（项目没有 <c>InternalsVisibleTo</c> 的先例，不为一个辅助方法开先河）。
/// </para>
/// </remarks>
public static class MailBodyParts
{
    /// <summary>递归列出所有**正文**文本部分（<c>text/plain</c> + <c>text/html</c>），跳过附件。</summary>
    public static IEnumerable<BodyPartText> TextParts(BodyPart? part)
    {
        switch (part)
        {
            case BodyPartText text when !text.IsAttachment:
                yield return text;
                break;

            case BodyPartMultipart multipart:
                foreach (var child in multipart.BodyParts)
                {
                    foreach (var nested in TextParts(child))
                    {
                        yield return nested;
                    }
                }

                break;

            // rfc822 嵌套（转发成附件的那种）：正文在内层，仍然不碰它的附件。
            case BodyPartMessage message:
                foreach (var nested in TextParts(message.Body))
                {
                    yield return nested;
                }

                break;
        }
    }

    /// <summary>找 <c>text/html</c> 正文部分；没有就返回 <see langword="null"/>。</summary>
    public static BodyPartText? FindHtml(BodyPart? structure)
    {
        foreach (var part in TextParts(structure))
        {
            if (part.IsHtml)
            {
                return part;
            }
        }

        return null;
    }
}
