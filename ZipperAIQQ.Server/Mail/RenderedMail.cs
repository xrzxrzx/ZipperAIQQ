using ZipperAIQQ.Agent.Mail;

namespace ZipperAIQQ.Server.Mail;

/// <summary>
/// 一封邮件的渲染结果：图 + 引擎说明 + 是否被上限截断。
/// </summary>
/// <param name="Images">产出的图（至少一张）。</param>
/// <param name="Engine">实际用的内核（<c>chromium</c> / <c>msedge</c> / <c>chrome</c>），排查用。</param>
/// <param name="Truncated">是否因为张数/高度上限只给了前几段。</param>
/// <param name="PageHeight">整页高度（像素，0 = 没量到）。</param>
public sealed record RenderedMail(
    IReadOnlyList<MailRenderImage> Images,
    string Engine,
    bool Truncated,
    int PageHeight);
