namespace ZipperAIQQ.Storage.Billing;

/// <summary>余额采样历史（窗口花费靠相邻采样差推算）。</summary>
public interface IBalanceStore
{
    Task AppendAsync(BalanceSample sample, CancellationToken ct = default);

    /// <summary>取某供应商在 <paramref name="since"/> 之后的采样（按时间正序）。</summary>
    Task<IReadOnlyList<BalanceSample>> ListSinceAsync(
        string provider, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>最近一次采样；没有则 null。</summary>
    Task<BalanceSample?> LatestAsync(string provider, CancellationToken ct = default);

    /// <summary>删掉早于 <paramref name="before"/> 的采样；返回删除条数。</summary>
    Task<int> PurgeBeforeAsync(DateTimeOffset before, CancellationToken ct = default);
}
