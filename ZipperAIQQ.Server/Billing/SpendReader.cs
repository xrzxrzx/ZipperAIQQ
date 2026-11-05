using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Server.Metrics;
using ZipperAIQQ.Storage.Billing;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Server.Billing;

/// <summary>
/// 「当前窗口花了多少」的读取实现：**余额差优先，token 估算保底**。
/// </summary>
/// <remarks>
/// <para>
/// 余额差是账实口径（采样不足 2 条时算不出来）；估算用同一窗口的 token × 价目表，
/// 虽然不如账单准，但足够回答「要不要拦」。
/// </para>
/// <para>
/// <b>没有数据时返回 <c>HasData=false</c></b>：闸门据此放行 —— 宁可少拦，也不能因为
/// 「还没采到余额」就把真人消息挡掉。
/// </para>
/// </remarks>
public sealed class SpendReader : ISpendReader
{
    private readonly IBalanceStore _balance;
    private readonly IMetricsStore _metrics;
    private readonly QuotaOptions _options;

    public SpendReader(IBalanceStore balance, IMetricsStore metrics, QuotaOptions options)
    {
        ArgumentNullException.ThrowIfNull(balance);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);

        _balance = balance;
        _metrics = metrics;
        _options = options;
    }

    public async Task<SpendSnapshot> ReadAsync(CancellationToken ct = default)
    {
        var now = BeijingTime.Now;
        var since = WindowStart(now, _options.Window);
        var hours = Math.Max(1, (int)Math.Ceiling((now - since).TotalHours));

        var samples = await _balance
            .ListSinceAsync(DeepSeekBalanceClient.Provider, since, ct)
            .ConfigureAwait(false);

        var remaining = samples.Count > 0 ? samples[^1].Total : (decimal?)null;

        if (SpendCalculator.HasEnoughSamples(samples))
        {
            return new SpendSnapshot(
                SpendCalculator.Window(samples), "balance", remaining, HasData: true);
        }

        // 保底：同一窗口的 token 估算
        var rows = await _metrics.GetSummaryAsync(hours, ct: ct).ConfigureAwait(false);
        var facts = MetricFacts.From(rows);
        if (facts.LlmCalls == 0)
        {
            return new SpendSnapshot(0m, "none", remaining, HasData: false);
        }

        var estimate = ModelPricing.DeepSeekChat.Estimate(
            facts.InputTokens, facts.OutputTokens, facts.CachedInputTokens);

        return new SpendSnapshot(estimate, "estimate", remaining, HasData: true);
    }

    /// <summary>窗口起点（北京时间：小时对齐、当天零点、当月一号）。</summary>
    private static DateTimeOffset WindowStart(DateTimeOffset now, string window) => window.ToLowerInvariant() switch
    {
        "hour" => new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset),
        "month" => new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset),
        _ => new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset),
    };
}
