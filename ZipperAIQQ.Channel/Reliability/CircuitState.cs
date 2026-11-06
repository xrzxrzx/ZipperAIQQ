namespace ZipperAIQQ.Channel.Reliability;

/// <summary>
/// 通道可用性的三态（熔断器语义）。
/// </summary>
/// <remarks>
/// 命名沿用熔断器：<see cref="Closed"/> = 正常放行，<see cref="Open"/> = 停止尝试，
/// <see cref="HalfOpen"/> = 放一次探测探路。**不要**和 WS 的连接状态混为一谈 ——
/// 那个只有 Open/Closed，而且没有「失败阈值」这一层。
/// </remarks>
public enum CircuitState
{
    /// <summary>正常：允许发送。</summary>
    Closed = 0,

    /// <summary>熔断：连续失败到阈值，暂停发送 / 暂停调模型。</summary>
    Open = 1,

    /// <summary>半开：冷却结束，放一次探测。</summary>
    HalfOpen = 2,
}
