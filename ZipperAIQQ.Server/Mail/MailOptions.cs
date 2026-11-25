namespace ZipperAIQQ.Server.Mail;

/// <summary>邮件汇总的节奏（决策九：单封即转 / 每 N 分钟合并 / 每日汇总）。</summary>
public enum MailDigestMode
{
    /// <summary>每封摘要完成就转。</summary>
    Immediate = 0,

    /// <summary>每 N 分钟把这段时间的摘要合并成一条。</summary>
    Interval = 1,

    /// <summary>每天固定时刻汇总一次。</summary>
    Daily = 2,
}

/// <summary>
/// 邮件接入的运行参数（<c>config/server.yaml</c> 的 <c>mail</c> 节）。
/// </summary>
/// <remarks>
/// <para>
/// **账号与凭据不在这里**：那属于用户数据，存在 <c>MailAccounts</c> 表里（客户端可改），
/// 这里只放"怎么跑"的策略参数（对齐 M2-B 的引导层 / 运行层分离）。
/// </para>
/// <para>
/// 默认值刻意保守：首次同步只拉 7 天、轮询 60 秒、摘要并发 2 —— 免得一开机就把
/// 三个邮箱的历史全拉下来、再同时打爆 LLM 配额。
/// </para>
/// </remarks>
public sealed record MailOptions
{
    /// <summary>总开关（关掉则不建任何 IMAP 连接，邮件相关 action 仍可读历史）。</summary>
    public bool Enabled { get; init; }

    /// <summary>首次同步回溯天数。</summary>
    public int InitialDays { get; init; } = 7;

    /// <summary>不支持 IDLE 时的轮询间隔（秒）。</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>正文截断上限（字符）。</summary>
    public int MaxBodyChars { get; init; } = 20_000;

    /// <summary>是否做摘要（关掉则只收信落库 + 转发原文摘要位为空）。</summary>
    public bool SummaryEnabled { get; init; } = true;

    /// <summary>并发摘要上限（避免打爆 DeepSeek 配额）。</summary>
    public int MaxConcurrentSummaries { get; init; } = 2;

    /// <summary>
    /// 汇总是否**再经一遍人格 agent**（默认开）。
    /// </summary>
    /// <remarks>
    /// 开：汇总先交主人私聊的 agent，用当前人设的语气转告（多一次模型调用）；
    /// 关：直接发汇总文本（省一次调用，但语气是"系统报表"）。
    /// 客户端可热改（<c>MailOptionsService.PersonaDigest</c>），这里是启动兜底。
    /// </remarks>
    public bool PersonaDigest { get; init; } = true;

    /// <summary>摘要失败重试次数（指数退避）。</summary>
    public int SummaryRetryCount { get; init; } = 3;

    /// <summary>汇总节奏。</summary>
    public MailDigestMode DigestMode { get; init; } = MailDigestMode.Immediate;

    /// <summary><see cref="MailDigestMode.Interval"/> 的间隔分钟数。</summary>
    public int DigestIntervalMinutes { get; init; } = 30;

    /// <summary><see cref="MailDigestMode.Daily"/> 的触发时刻（北京时间 0-23）。</summary>
    public int DailyAtHour { get; init; } = 20;

    /// <summary>转发失败重试次数。</summary>
    public int ForwardRetryCount { get; init; } = 3;

    // ───────────── 双通道（B3）：渲染画面 / 原文直读 ─────────────

    /// <summary>
    /// 是否把 <c>mail_render</c> 工具给模型（默认**关**，方案 §6.4 / D-09）。
    /// </summary>
    /// <remarks>
    /// 关的是「工具存不存在」，不是「渲染开不开」：开着也不会有任何自动渲染 ——
    /// 只有模型显式调用才截一张图。默认关是因为它依赖外置 Chromium，
    /// 没装好的机器上多一个只会失败的工具等于多一条幻觉入口。
    /// </remarks>
    public bool RenderEnabled { get; init; }

    /// <summary>
    /// HTML 正文落库的上限（KB，默认 256）。**与渲染开关无关**，一直生效。
    /// </summary>
    /// <remarks>
    /// 为什么一律存：163 会清过期件，等模型想渲染时邮件可能已经不在服务器上；
    /// 但也不能无限存 —— 一封带内联 base64 图的邮件能有几十 MB。
    /// </remarks>
    public int RenderMaxBodyHtmlKb { get; init; } = 256;

    /// <summary>单次渲染最多给几张图（默认 4，长邮件按高度切段）。</summary>
    public int RenderMaxImages { get; init; } = 4;

    /// <summary>
    /// 同时进行的渲染数（默认 1，上限 4）。
    /// </summary>
    /// <remarks>
    /// 每个渲染就是一个 Chromium 进程：8 GB 的机器上并发 2 以上就可能被 OOM 掉整个服务端
    /// （方案 §4/B3 风险表：渲染吃内存 → 并发 1）。要放宽请先看内存余量。
    /// </remarks>
    public int RenderMaxConcurrent { get; init; } = 1;

    /// <summary>渲染超时（秒，默认 30）。</summary>
    public int RenderTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// 是否允许渲染时加载远程资源（默认**否**：防跟踪像素）。
    /// </summary>
    public bool RenderAllowRemote { get; init; }

    /// <summary>渲染脚本路径（相对仓库根或绝对路径）。</summary>
    public string RenderScriptPath { get; init; } = "tools/mail-render/render.mjs";

    /// <summary>
    /// 渲染临时目录（每次调用一个子目录，跑完就删）。
    /// </summary>
    /// <remarks>
    /// 刻意**不用系统 TEMP**：部署机上 TEMP 常被安全策略收紧（实测在 DSH 沙箱下
    /// 创建子目录会被拒），而 <c>data/</c> 是我们自己的地盘，权限与清理都可控。
    /// </remarks>
    public string RenderTempDir { get; init; } = "./data/mail-render-tmp";

    /// <summary>Node 可执行文件（默认取 PATH 里的 <c>node</c>）。</summary>
    public string RenderNodePath { get; init; } = "node";

    /// <summary>
    /// 渲染用的 playwright 模块解析目录（默认空 = 用脚本内置的 <c>douyin-web-sidecar</c>）。
    /// </summary>
    /// <remarks>给"sidecar 被搬到别处 / 单独装了 playwright"的部署留个口子。</remarks>
    public string RenderPlaywrightDir { get; init; } = string.Empty;

    /// <summary>读原文时正文的字符上限（默认 20000）。</summary>
    public int RawMaxChars { get; init; } = 20_000;
}
