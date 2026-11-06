namespace ZipperAIQQ.Channel.Reliability;

/// <summary>
/// 重连退避策略（平台无关，纯计算）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么抽出来：QQ 的 WS 要重拨、抖音的是 sidecar 反向连入、以后接别的平台也各有各的重连动作，
/// 但「退避多久、什么时候放弃、什么时候半开探测」是同一套。重复实现的结局是每个平台行为都不一样，
/// 排障时还得先问「这是哪个通道的重连」。
/// </para>
/// <para>
/// 退避：<c>BaseDelay × 2^attempt</c>，封顶 <see cref="MaxDelay"/>。
/// 默认 1s → 2s → 4s → 8s → 16s → 30s（封顶），不会把恢复拖到几分钟。
/// </para>
/// </remarks>
public sealed record ReconnectPolicy(TimeSpan BaseDelay, TimeSpan MaxDelay)
{
    /// <summary>默认：1 秒起，30 秒封顶。</summary>
    public static ReconnectPolicy Default { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

    /// <summary>第 <paramref name="attempt"/> 次（0 起）重试前该等多久。</summary>
    public TimeSpan DelayFor(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BaseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDelay, BaseDelay);

        // 移位会溢出，先封住指数
        var exponent = Math.Min(attempt, 16);
        var rawMs = BaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(rawMs, MaxDelay.TotalMilliseconds));
    }

    /// <summary>
    /// 带抖动的退避：多个通道同时掉线时避免同一秒一起重连。
    /// </summary>
    /// <param name="attempt">第几次重试（0 起）。</param>
    /// <param name="random">注入以便测试可复现。</param>
    /// <param name="ratio">抖动比例（默认 ±20%）。</param>
    public TimeSpan DelayFor(int attempt, Random random, double ratio = 0.2)
    {
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratio);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ratio, 1);

        var baseDelay = DelayFor(attempt);
        var factor = 1 + ((random.NextDouble() * 2 - 1) * ratio);
        var ms = baseDelay.TotalMilliseconds * factor;
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 100, MaxDelay.TotalMilliseconds));
    }
}
