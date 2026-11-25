namespace ZipperAIQQ.Server.Mail;

/// <summary>
/// 邮件渲染器（外置 Chromium 的薄壳）。
/// </summary>
/// <remarks>
/// 抽成接口的唯一目的是可测：真实现要 spawn 一个 Node 进程，单测里既慢又依赖环境
/// （CI 机器上未必装了 Node / 浏览器）。<see cref="MailReadService"/> 只依赖这个窄面。
/// </remarks>
public interface IMailRenderer
{
    /// <summary>渲染器是否可用（开关 + 脚本 + Node 三者都在）。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用的人话原因；可用时为空串。</summary>
    string UnavailableReason { get; }

    /// <summary>
    /// 把 HTML 渲染成图片。
    /// </summary>
    /// <param name="html">邮件 HTML 源文本。</param>
    /// <param name="segments">true = 按高度切段。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidOperationException">渲染器不可用，或外置脚本回报失败。</exception>
    Task<RenderedMail> RenderAsync(string html, bool segments, CancellationToken ct);
}
