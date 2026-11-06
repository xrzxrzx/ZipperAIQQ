namespace ZipperAIQQ.Storage.Alerts;

/// <summary>告警投递记录（写入 + 去重查询 + 最近列表）。</summary>
public interface IAlertStore
{
    Task AppendAsync(AlertRecord record, CancellationToken ct = default);

    /// <summary>同一 (Kind, Platform) 在 <paramref name="since"/> 之后是否已经投递成功过。</summary>
    Task<bool> ExistsRecentAsync(
        string kind,
        string? platform,
        DateTimeOffset since,
        CancellationToken ct = default);

    /// <summary>最近若干条（新的在前，供客户端/排障查看）。</summary>
    Task<IReadOnlyList<AlertRecord>> ListRecentAsync(int limit, CancellationToken ct = default);
}
