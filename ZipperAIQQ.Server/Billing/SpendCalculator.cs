using ZipperAIQQ.Storage.Billing;

namespace ZipperAIQQ.Server.Billing;

/// <summary>
/// 用余额采样差推算窗口花费。
/// </summary>
/// <remarks>
/// <para>
/// 直接拿「首尾 total 差」会被**充值**干扰：充 100 会让差额变成负数。
/// 所以要先扣掉充值/赠金的增量：<c>花费 = (startTotal - endTotal) + (endToppedUp - startToppedUp)</c>。
/// </para>
/// <para>
/// 采样不足 2 条时返回 0（不是「没花钱」，而是「测不出来」）—— 调用方要用 <c>null</c> 表示不可知，
/// 这里保持 0 是为了累加方便，另配 <see cref="HasEnoughSamples"/> 判断。
/// </para>
/// </remarks>
public static class SpendCalculator
{
    /// <summary>两条采样之间花了多少钱（≥ 0）。</summary>
    public static decimal Between(BalanceSample start, BalanceSample end)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);

        var spent = (start.Total - end.Total) + (end.ToppedUp - start.ToppedUp);
        return spent < 0 ? 0 : spent;
    }

    /// <summary>窗口内总花费（按时间正序的首尾两条算）。</summary>
    public static decimal Window(IReadOnlyList<BalanceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count < 2)
        {
            return 0;
        }

        var ordered = samples.OrderBy(s => s.SampledAt).ToList();
        return Between(ordered[0], ordered[^1]);
    }

    /// <summary>采样数量够不够算出一个窗口花费。</summary>
    public static bool HasEnoughSamples(IReadOnlyList<BalanceSample> samples) =>
        samples is { Count: >= 2 };
}
