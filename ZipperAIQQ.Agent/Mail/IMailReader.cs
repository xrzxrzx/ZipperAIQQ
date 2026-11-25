namespace ZipperAIQQ.Agent.Mail;

/// <summary>
/// 邮件「双通道」读入口（问题记录 06 §1 / B3）：渲染画面 + 原文直读。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要抽接口：Agent 不能依赖 Server（依赖方向硬约束），而真正干活的东西
/// （IMAP 会话、Chromium、审计落库）全在 Server。与本项目既有的
/// <c>IWebSearchProvider</c> / <c>IDeepSeekMediaClient</c> 是同一条老路。
/// </para>
/// <para>
/// ⚠️ **两条通道刻意不合并**（用户明确要求）：<see cref="RenderAsync"/> 给的是「眼睛看到的画面」，
/// <see cref="ReadRawAsync"/> 给的是「邮件的原始文本」。关掉一个不影响另一个 ——
/// 合并成一个「读邮件」工具，就没法表达"只看画面、别把原文倒出来"这种意图。
/// </para>
/// <para>
/// **许可与否不在这里判**：权限门禁在 <c>MailTools</c> + <c>PermissionGate</c>。
/// 实现方只负责「取回来 / 说清为什么取不回来」，避免两处各写一套口径。
/// </para>
/// </remarks>
public interface IMailReader
{
    /// <summary>渲染通道是否可用（外置件缺失时为 <see langword="false"/>，工具不出现）。</summary>
    bool CanRender { get; }

    /// <summary>渲染不可用的人话原因（给模型看，免得它反复试）。可用时为空串。</summary>
    string RenderUnavailableReason { get; }

    /// <summary>
    /// 按「主键或关键词」定位邮件。
    /// </summary>
    /// <param name="reference">
    /// 模型手里的引用：数字主键（<c>1234</c>）或关键词（"银行"、"账单"）。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的候选（0 条 = 没找到，多条 = 需要模型自己挑）。</returns>
    Task<IReadOnlyList<MailBrief>> LocateAsync(string reference, CancellationToken ct = default);

    /// <summary>把邮件正文渲染成图片（复用 B1 的 Chromium，默认禁脚本、禁外链）。</summary>
    /// <param name="mailId">邮件主键（来自 <see cref="LocateAsync"/>）。</param>
    /// <param name="segments">true = 长图按高度切段（每段一张）；false = 只给第一段。</param>
    /// <param name="ct">取消令牌。</param>
    Task<MailRenderOutcome> RenderAsync(long mailId, bool segments, CancellationToken ct = default);

    /// <summary>读邮件原文（头 + 正文源文本；不含附件内容）。</summary>
    /// <param name="mailId">邮件主键。</param>
    /// <param name="maxChars">正文的字符上限；超出截断并标注。</param>
    /// <param name="ct">取消令牌。</param>
    Task<MailRawOutcome> ReadRawAsync(long mailId, int maxChars, CancellationToken ct = default);
}
