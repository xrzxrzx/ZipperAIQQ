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
    /// <summary>追加一条**收到的**消息，返回入库主键。</summary>
    Task<long> AppendAsync(IncomingMessage message, CancellationToken ct = default);

    /// <summary>
    /// 追加一条**发出的**消息（Direction=1），返回入库主键。
    /// </summary>
    /// <remarks>
    /// 为什么 AI 发的也要存：客户端要能看「含 AI 发出的」聊天记录（FR-20.3），
    /// 只存收到的会让人误判「它到底回没回」。
    /// </remarks>
    Task<long> AppendOutgoingAsync(
        OutgoingMessage message,
        string senderUserId,
        DateTimeOffset sentAt,
        CancellationToken ct = default);

    /// <summary>
    /// 取某会话最近 <paramref name="limit"/> 条消息，**按时间正序**返回。
    /// </summary>
    Task<IReadOnlyList<StoredMessage>> GetRecentAsync(string sessionId, int limit, CancellationToken ct = default);

    /// <summary>某会话的消息总数。</summary>
    Task<long> CountAsync(string sessionId, CancellationToken ct = default);
}
