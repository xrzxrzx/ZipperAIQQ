namespace ZipperAIQQ.Storage.Turns;

/// <summary>轮次审计（K-5）。</summary>
public interface ITurnLogStore
{
    Task AppendAsync(TurnLog log, CancellationToken ct = default);

    Task<IReadOnlyList<TurnLog>> ListRecentAsync(int limit = 20, CancellationToken ct = default);
}
