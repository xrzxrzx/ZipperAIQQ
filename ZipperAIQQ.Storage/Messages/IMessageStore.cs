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
    /// 取某个会话最近 <paramref name="limit"/> 条消息，**按时间正序**返回。
    /// </summary>
    Task<IReadOnlyList<StoredMessage>> GetRecentAsync(string sessionId, int limit, CancellationToken ct = default);

    /// <summary>
    /// 按时间区间取消息（起止都可空），**按时间正序**返回。
    /// </summary>
    /// <remarks>
    /// 给「会话导出按区间」用（问题记录 #37）：不能在内存里过滤已加载的 100 条，
    /// 那既漏数据又受上次加载限制。
    /// </remarks>
    Task<IReadOnlyList<StoredMessage>> GetRangeAsync(
        string sessionId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken ct = default);

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

    /// <summary>按平台消息 ID 查一条（撤回反馈用）。找不到返回 null。</summary>
    Task<StoredMessage?> FindByMessageIdAsync(
        string sessionId,
        string messageId,
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
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default);

    /// <summary>
    /// 按关键词搜**所有会话**的历史（跨会话）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 给 <c>search_other_sessions</c> 工具用：让 AI 能回答「谁跟你说过什么」，
    /// 从而更像一个跨聊天的实体，而不是只活在某一个会话里。
    /// </para>
    /// <para>
    /// ⚠️ 这是**跨会话**读别人的对话 —— 权限门禁默认只给管理员/主人，
    /// 并且实现上必须能排除当前会话（避免和自己重复）。
    /// </para>
    /// </remarks>
    /// <param name="keyword">关键词（LIKE 模糊匹配）。</param>
    /// <param name="limit">最多几条。</param>
    /// <param name="excludeSessionId">要排除的会话（通常是当前会话），可为 null。</param>
    /// <param name="platform">
    /// 只看某个平台（<c>qq</c> / <c>douyin</c>）；<c>null</c> = 全部平台。
    /// </param>
    /// <remarks>
    /// S3-G / 决策十：多平台后"跨会话搜索"天然是跨平台的，所以要能按平台收窄 ——
    /// 模型答"QQ 群里谁提过 X"时不该被抖音消息干扰。
    /// </remarks>
    Task<IReadOnlyList<StoredMessage>> SearchAcrossSessionsAsync(
        string keyword,
        int limit,
        string? excludeSessionId = null,
        string? platform = null,
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
