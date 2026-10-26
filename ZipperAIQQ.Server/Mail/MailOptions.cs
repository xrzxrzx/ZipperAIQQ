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
}
