using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Storage.Messages;

/// <summary>
/// 消息持久化。
/// </summary>
/// <remarks>
/// M1-A 只要求「收得到、存得下、查得回」。
/// 全文检索（FTS5）与跨会话检索在 **M1-C** 补，不要在 M1-A 提前实现。
/// </remarks>
public interface IMessageStore
{
    /// <summary>追加一条消息，返回入库主键。</summary>
    Task<long> AppendAsync(IncomingMessage message, CancellationToken ct = default);

    /// <summary>
    /// 取某会话最近 <paramref name="limit"/> 条消息，**按时间正序**返回。
    /// </summary>
    Task<IReadOnlyList<StoredMessage>> GetRecentAsync(string sessionId, int limit, CancellationToken ct = default);

    /// <summary>某会话的消息总数。</summary>
    Task<long> CountAsync(string sessionId, CancellationToken ct = default);
}
