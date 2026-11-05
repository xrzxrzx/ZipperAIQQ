namespace ZipperAIQQ.Server.Billing;

/// <summary>
/// 本地价目表（**token 保底口径**：余额接口不可用时的估算依据）。
/// </summary>
/// <remarks>
/// <para>
/// 单位统一为「元 / 百万 token」。价格必须人工维护 —— 供应商调价不会通知我们，
/// 所以这里只是**估算**，账单真值永远看余额差或供应商后台。
/// </para>
/// <para>
/// 默认值取 DeepSeek 公开价（2025 年口径）：输入未命中 ¥2、命中缓存 ¥0.5、输出 ¥8。
/// </para>
/// </remarks>
public sealed record ModelPricing(
    decimal InputPerMillion,
    decimal CachedInputPerMillion,
    decimal OutputPerMillion)
{
    public static ModelPricing DeepSeekChat { get; } = new(2m, 0.5m, 8m);

    /// <summary>按 token 估算金额（元）。缓存命中部分按更低价计。</summary>
    public decimal Estimate(long inputTokens, long outputTokens, long cachedInputTokens)
    {
        var cached = Math.Clamp(cachedInputTokens, 0, Math.Max(0, inputTokens));
        var uncached = Math.Max(0, inputTokens - cached);

        return (uncached / 1_000_000m * InputPerMillion)
             + (cached / 1_000_000m * CachedInputPerMillion)
             + (Math.Max(0, outputTokens) / 1_000_000m * OutputPerMillion);
    }
}
