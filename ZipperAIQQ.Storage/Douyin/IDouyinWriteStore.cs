namespace ZipperAIQQ.Storage.Douyin;

/// <summary>
/// 抖音写动作的仓储（配额 + 留痕），B5。
/// </summary>
/// <remarks>
/// 查询口径都是「**24 小时滚动窗口**」而不是"自然日"：自然日会在午夜整点把配额清零，
/// 于是同一个人可以在 23:59 与 00:01 各写一次 —— 那正好是风控最敏感的形状。
/// </remarks>
public interface IDouyinWriteStore
{
    /// <summary>追加一条（返回新行 id）。**失败也要写**（被闸门拒绝也算事实）。</summary>
    Task<long> AppendAsync(DouyinWriteAction action, CancellationToken ct = default);

    /// <summary>某动作在窗口内的条数（配额用）。</summary>
    Task<int> CountSinceAsync(string action, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>某动作在窗口内**最早**的一条时间（算「配额什么时候回血」用）。</summary>
    Task<DateTimeOffset?> OldestSinceAsync(string action, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>某动作最后一次的时间（算「离上次写多久」用）。</summary>
    Task<DateTimeOffset?> LastAtAsync(string action, CancellationToken ct = default);

    /// <summary>某动作是否在窗口内对同一目标写过（「同人一次」用）。</summary>
    Task<bool> ExistsForTargetAsync(
        string action, string targetKey, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>最近的写动作（诊断 / 主人核账用）。</summary>
    Task<IReadOnlyList<DouyinWriteAction>> ListRecentAsync(int limit, CancellationToken ct = default);
}
