using ZipperAIQQ.Server.Billing;
using ZipperAIQQ.Storage.Billing;

namespace ZipperAIQQ.Tests.Server;

/// <summary>E 批纯核心：余额解析 / 花费推算 / token 计价。</summary>
public sealed class BillingCoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 0, 0, 0, TimeSpan.FromHours(8));

    private static BalanceSample Sample(decimal total, decimal toppedUp, int minutes) =>
        new("deepseek", "CNY", total, 0, toppedUp, true, Now.AddMinutes(minutes));

    // ── 解析 ──

    [Fact]
    public void Parses_deepseek_balance_payload()
    {
        const string json = """
            {"is_available":true,"balance_infos":[
              {"currency":"USD","total_balance":"1.00","granted_balance":"0.00","topped_up_balance":"1.00"},
              {"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"}
            ]}
            """;

        var sample = DeepSeekBalanceClient.Parse(json, Now);

        Assert.NotNull(sample);
        Assert.Equal("CNY", sample.Currency);        // 优先 CNY
        Assert.Equal(110m, sample.Total);
        Assert.Equal(10m, sample.Granted);
        Assert.Equal(100m, sample.ToppedUp);
        Assert.True(sample.IsAvailable);
    }

    [Fact]
    public void Missing_balance_infos_returns_null() =>
        Assert.Null(DeepSeekBalanceClient.Parse("""{"is_available":true}""", Now));

    [Fact]
    public void Broken_json_returns_null() =>
        Assert.Null(DeepSeekBalanceClient.Parse("{not json", Now));

    // ── 花费推算 ──

    [Fact]
    public void Spend_is_total_difference()
    {
        Assert.Equal(3m, SpendCalculator.Between(Sample(100, 100, 0), Sample(97, 100, 10)));
    }

    [Fact]
    public void Top_up_is_not_counted_as_spend()
    {
        // 余额从 100 变 197、同期充值 +100 → 实际花费 3
        Assert.Equal(3m, SpendCalculator.Between(Sample(100, 100, 0), Sample(197, 200, 10)));
    }

    [Fact]
    public void Negative_difference_clamps_to_zero()
    {
        // 赠金到账之类会让「差」为负，不能报成「赚了钱」
        Assert.Equal(0m, SpendCalculator.Between(Sample(100, 100, 0), Sample(120, 100, 10)));
    }

    [Fact]
    public void Window_needs_two_samples()
    {
        Assert.Equal(0m, SpendCalculator.Window([Sample(100, 100, 0)]));
        Assert.False(SpendCalculator.HasEnoughSamples([Sample(100, 100, 0)]));
        Assert.True(SpendCalculator.HasEnoughSamples([Sample(100, 100, 0), Sample(99, 100, 5)]));
    }

    // ── token 计价（保底口径） ──

    [Fact]
    public void Pricing_uses_cached_rate_for_cached_tokens()
    {
        // 2 元/M 输入、0.5 元/M 缓存、8 元/M 输出
        var pricing = ModelPricing.DeepSeekChat;

        var cost = pricing.Estimate(inputTokens: 1_000_000, outputTokens: 100_000, cachedInputTokens: 400_000);

        // 未命中 0.6M × 2 = 1.2；缓存 0.4M × 0.5 = 0.2；输出 0.1M × 8 = 0.8
        Assert.Equal(2.2m, cost);
    }

    [Fact]
    public void Cached_tokens_are_clamped_to_input()
    {
        var pricing = ModelPricing.DeepSeekChat;

        var cost = pricing.Estimate(1000, 0, 5000);

        // 缓存数被夹到输入上限：1000 token 全按缓存价 0.5 元/M
        Assert.Equal(0.0005m, cost);
    }
}
