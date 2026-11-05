namespace ZipperAIQQ.Storage.Billing;

/// <summary>
/// 一次余额采样（供应商只给**快照**，没有历史 —— 历史只能由我们采样差得出）。
/// </summary>
/// <param name="Provider">供应商标识（目前只有 <c>deepseek</c>）。</param>
/// <param name="Currency">币种（CNY / USD）。</param>
/// <param name="Total">总可用余额（含赠金与充值）。</param>
/// <param name="Granted">未过期赠金。</param>
/// <param name="ToppedUp">充值余额。</param>
/// <param name="IsAvailable">供应商自报「余额是否够用」。</param>
/// <param name="SampledAt">采样时间。</param>
public sealed record BalanceSample(
    string Provider,
    string Currency,
    decimal Total,
    decimal Granted,
    decimal ToppedUp,
    bool IsAvailable,
    DateTimeOffset SampledAt);
