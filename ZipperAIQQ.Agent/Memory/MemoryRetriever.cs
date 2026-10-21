using Serilog;
using ZipperAIQQ.Agent.Embedding;
using ZipperAIQQ.Storage.Embeddings;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Agent.Memory;

/// <summary>
/// 长期记忆检索：把「很久以前说过的话」按语义找回来。
/// </summary>
/// <remarks>
/// <para>
/// 与热窗的分工：热窗负责**连贯**（最近几条原样给），本类负责**召回**（很久前提过的事）。
/// 只靠热窗，聊过 30 轮就会「忘记你叫什么」。
/// </para>
/// <para>
/// **为什么用 top-k 而不是相似度阈值**：实测中文短文本即使无关也有 ~0.3 基线，
/// 有效召回在 0.6 左右。设 0.7 之类的硬阈值会把真命中的也过滤掉。
/// </para>
/// </remarks>
public sealed class MemoryRetriever
{
    /// <summary>向量来源标识。换模型后必须整批重建 —— 不同模型的向量不在同一空间。</summary>
    public const string ModelVersion = "bge-small-zh-v1.5/512";

    private readonly IEmbeddingService _embedding;
    private readonly IEmbeddingStore _vectors;
    private readonly IMessageStore _messages;
    private readonly ILogger _logger;

    public MemoryRetriever(
        IEmbeddingService embedding,
        IEmbeddingStore vectors,
        IMessageStore messages,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(logger);

        _embedding = embedding;
        _vectors = vectors;
        _messages = messages;
        _logger = logger;
    }

    /// <summary>
    /// 给会话里还没算向量的消息补算（每次最多一批，避免一次跑太久）。
    /// </summary>
    /// <returns>本次补算的条数。</returns>
    public async Task<int> BackfillAsync(string sessionId, int maxBatch = 50, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var pending = await _vectors.GetPendingAsync(sessionId, maxBatch, ct).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var item in pending)
        {
            try
            {
                var vector = _embedding.Embed(item.Text);
                await _vectors.SaveAsync(item.MessageId, sessionId, vector, ModelVersion, ct).ConfigureAwait(false);
                count++;
            }
            catch (Exception ex)
            {
                // 单条失败不该让整批停下
                _logger.Warning(ex, "消息 #{Id} 向量化失败", item.MessageId);
            }
        }

        _logger.Debug("会话 {SessionId} 补算向量 {Count} 条", sessionId, count);
        return count;
    }

    /// <summary>
    /// 按语义检索该会话的历史消息。
    /// </summary>
    /// <param name="excludeRecentCount">
    /// 排除最近这么多条 —— 它们已经在热窗里，重复放进去只会浪费 token。
    /// </param>
    public async Task<IReadOnlyList<StoredMessage>> RetrieveAsync(
        string sessionId,
        string query,
        int topK = 5,
        int excludeRecentCount = 10,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var queryVector = _embedding.Embed(query);

        // 多取一些，因为要剔除已经在热窗里的
        var hits = await _vectors
            .SearchAsync(sessionId, queryVector, topK + excludeRecentCount, ct)
            .ConfigureAwait(false);

        if (hits.Count == 0)
        {
            return [];
        }

        var recent = await _messages.GetRecentAsync(sessionId, excludeRecentCount, ct).ConfigureAwait(false);
        var recentIds = recent.Select(r => r.Id).ToHashSet();

        var picked = hits.Where(h => !recentIds.Contains(h.MessageId)).Take(topK).ToList();
        if (picked.Count == 0)
        {
            return [];
        }

        // 取消息正文（保持时间顺序，读起来才像一段对话而不是碎片）
        var byId = new Dictionary<long, StoredMessage>();
        foreach (var stored in recent)
        {
            byId[stored.Id] = stored;
        }

        var missing = picked.Where(p => !byId.ContainsKey(p.MessageId)).Select(p => p.MessageId).ToList();
        if (missing.Count > 0)
        {
            var fetched = await FetchByIdsAsync(sessionId, missing, ct).ConfigureAwait(false);
            foreach (var stored in fetched)
            {
                byId[stored.Id] = stored;
            }
        }

        return picked
            .Where(p => byId.ContainsKey(p.MessageId))
            .Select(p => byId[p.MessageId])
            .OrderBy(m => m.Message.OccurredAt)
            .ToList();
    }

    /// <summary>按主键取消息（走消息仓储，保证与热窗同一数据源）。</summary>
    private Task<IReadOnlyList<StoredMessage>> FetchByIdsAsync(
        string sessionId,
        IReadOnlyList<long> ids,
        CancellationToken ct) =>
        _messages.GetByIdsAsync(sessionId, ids, ct);
}
