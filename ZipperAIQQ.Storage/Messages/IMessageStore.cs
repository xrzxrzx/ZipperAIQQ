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
    /// 追加一条**命令响应**（<c>Direction=2</c>）。
    /// </summary>
    /// <remarks>
    /// 与 AI 发言分开存（FR-9.4）：命令结果不能伪装成"AI 自己说的话"，
    /// 否则后续对话里模型会误以为那是它说的。
    /// </remarks>
    Task<long> AppendCommandAsync(
        OutgoingMessage message,
        string senderUserId,
        DateTimeOffset sentAt,
        CancellationToken ct = default);

    /// <summary>
    /// 取某会话最近 <paramref name="limit"/> 条消息，**按时间正序**返回。
    /// </summary>
    Task<IReadOnlyList<StoredMessage>> GetRecentAsync(string sessionId, int limit, CancellationToken ct = default);

    /// <summary>
    /// 按主键批量取消息（检索命中后用）。
    /// </summary>
    /// <remarks>
    /// 不可以用「取最近 N 条再过滤」代替：命中的可能是很久以前的消息，
    /// 早被挤出最近窗口了，那样会静默漏掉最该召回的内容。
    /// </remarks>
    Task<IReadOnlyList<StoredMessage>> GetByIdsAsync(
        string sessionId,
        IReadOnlyList<long> ids,
        CancellationToken ct = default);

    /// <summary>某会话的消息总数。</summary>
    Task<long> CountAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 按关键词搜本会话历史（LIKE），按时间正序返回。
    /// </summary>
    /// <remarks>
    /// 给 agent 的 <c>search_history</c> 工具用：热窗之外的旧消息模型看不到，
    /// 没有这个工具它只能靠上下文瞎编（实测被用户抓到）。
    /// </remarks>
    Task<IReadOnlyList<StoredMessage>> SearchAsync(
        string sessionId,
        string keyword,
        int limit,
        CancellationToken ct = default);

    /// <summary>会话概览（按最后消息时间倒序），供 M2 客户端列出"当前在接收的聊天"。</summary>
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// 删除某会话的全部消息（<c>/purge</c>，**不可恢复**）。
    /// </summary>
    /// <returns>删除的条数。</returns>
    Task<int> PurgeAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>一个会话的概览。</summary>
public sealed record SessionSummary(
    string SessionId,
    int MessageCount,
    DateTimeOffset LastMessageAt);
