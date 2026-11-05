namespace ZipperAIQQ.Storage.Metrics;

/// <summary>一次调用的度量记录。</summary>
/// <param name="Kind">调用类别，如 <c>llm.call</c> / <c>tool.send_message</c> / <c>embedding</c>。</param>
/// <param name="ElapsedMs">耗时（毫秒）。</param>
/// <param name="InputTokens">输入 token（仅 LLM 调用有）。</param>
/// <param name="OutputTokens">输出 token（仅 LLM 调用有）。</param>
/// <param name="CachedInputTokens">
/// 命中缓存的输入 token（仅 LLM 调用有，来自 MEAI 的 <c>UsageDetails.CachedInputTokenCount</c>）。
/// 没采集到就是 <c>null</c>，**不要当成 0** —— 两者对"命中率"的含义完全不同。
/// </param>
public sealed record MetricEntry(
    string? SessionId,
    string Kind,
    double ElapsedMs,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? CachedInputTokens = null);

/// <summary>按类别聚合后的统计。</summary>
/// <param name="Kind">调用类别。</param>
/// <param name="Count">次数。</param>
/// <param name="AvgMs">平均耗时（毫秒）。</param>
/// <param name="MinMs">最小耗时。</param>
/// <param name="MaxMs">最大耗时。</param>
/// <param name="TotalInputTokens">输入 token 合计。</param>
/// <param name="TotalOutputTokens">输出 token 合计。</param>
/// <param name="TotalCachedInputTokens">命中缓存的输入 token 合计。</param>
/// <param name="P50Ms">
/// 中位数（最近秩法）。**没有它，平均值会把长尾抹平** —— 单看平均既看不出「偶尔很慢」，
/// 也看不出「一半都慢」。
/// </param>
/// <param name="P95Ms">95 分位（最近秩法）：比平均值更能说明「最慢那一小撮到底多慢」。</param>
public sealed record MetricSummary(
    string Kind,
    long Count,
    double AvgMs,
    double MinMs,
    double MaxMs,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCachedInputTokens = 0,
    double P50Ms = 0,
    double P95Ms = 0);

/// <summary>一个会话的指标概况（数据页的会话筛选下拉用）。</summary>
public sealed record MetricSessionInfo(string SessionId, long Calls, DateTimeOffset LastAt);

/// <summary>
/// 调用指标记录与聚合查询。
/// </summary>
/// <remarks>
/// <para>
/// 目的：客户端要能显示「哪类调用最慢 / token 烧得快不快」，并支持**按会话**看。
/// 记录是**追加**写，聚合是**按类别 GROUP BY**，排序由查询方决定。
/// </para>
/// <para>
/// 只记元数据（耗时、token 数），**不记消息正文** —— 后者是敏感内容。
/// </para>
/// </remarks>
public interface IMetricsStore
{
    Task RecordAsync(MetricEntry entry, CancellationToken ct = default);

    /// <summary>
    /// 取最近一段时间内、按类别聚合的统计。
    /// </summary>
    /// <remarks>
    /// 排序 = **次数降序、平均耗时降序**。旧实现只按平均耗时倒序，
    /// 于是「偶尔跑一次、一次上万毫秒」的类别永远霸占表头（问题记录 04 #3）。
    /// 长尾改用 <see cref="MetricSummary.P95Ms"/> 单独陈述，不靠排序强调。
    /// </remarks>
    /// <param name="windowHours">回看多少小时；&lt;= 0 表示不限时间。</param>
    /// <param name="sessionId">只统计该会话；<c>null</c> = 全部会话。</param>
    Task<IReadOnlyList<MetricSummary>> GetSummaryAsync(
        int windowHours = 24,
        string? sessionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// 有指标记录的会话（按最后活动时间倒序），供数据页的筛选下拉使用。
    /// </summary>
    Task<IReadOnlyList<MetricSessionInfo>> ListSessionAsync(
        int windowHours = 24,
        CancellationToken ct = default);
}
