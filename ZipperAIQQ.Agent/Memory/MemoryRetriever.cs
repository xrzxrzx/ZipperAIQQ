using System.Diagnostics;
using Serilog;
using ZipperAIQQ.Agent.Embedding;
using ZipperAIQQ.Storage.Embeddings;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;

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
    private readonly IMetricsStore _metrics;
    private readonly ILogger _logger;

    public MemoryRetriever(
        IEmbeddingService embedding,
        IEmbeddingStore vectors,
        IMessageStore messages,
        IMetricsStore metrics,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentNullException.ThrowIfNull(vectors);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _embedding = embedding;
        _vectors = vectors;
        _messages = messages;
        _metrics = metrics;
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

        var sw = Stopwatch.StartNew();
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
        sw.Stop();

        // 按**批次**记一行：一次补算可能几十上百条，按条记会把表迅速撑爆
        if (count > 0)
        {
            await RecordAsync(sessionId, "embedding.backfill", sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);
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

        var sw = Stopwatch.StartNew();
        try
        {
            return await RetrieveCoreAsync(sessionId, query, topK, excludeRecentCount, ct).ConfigureAwait(false);
        }
        finally
        {
            // 检索失败也要记耗时 —— 「检索慢了」本身就是最该被看见的信号
            sw.Stop();
            await RecordAsync(sessionId, "retrieval", sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<StoredMessage>> RetrieveCoreAsync(
        string sessionId,
        string query,
        int topK,
        int excludeRecentCount,
        CancellationToken ct)
    {
        var queryVector = _embedding.Embed(query);

        // excludeRecentCount <= 0 表示"不排除最近"（/search 命令用这个口径），
        // 此时不能调 GetRecentAsync(0) —— 那个方法要求 limit > 0
        var recentCount = Math.Max(0, excludeRecentCount);

        // 多取一些，因为要剔除已经在热窗里的
        var hits = await _vectors
            .SearchAsync(sessionId, queryVector, topK + recentCount, ct)
            .ConfigureAwait(false);

        if (hits.Count == 0)
        {
            return [];
        }

        var recent = recentCount > 0
            ? await _messages.GetRecentAsync(sessionId, recentCount, ct).ConfigureAwait(false)
            : [];

        var recentIds = recent.Select(r => r.Id).ToHashSet();

        var picked = hits.Where(h => !recentIds.Contains(h.MessageId)).Take(topK).ToList();

        // 命中率要能算（FR-10.3）：retrieval 记次数、retrieval.hit 记命中条数
        // （命中条数放在 InputTokens 字段里 —— 指标表不为它单独加列）
        if (picked.Count > 0)
        {
            await RecordHitAsync(sessionId, picked.Count, ct).ConfigureAwait(false);
        }

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

    /// <summary>记一行调用指标；失败就当观测丢了一次，绝不影响主流程。</summary>
    private async Task RecordAsync(string sessionId, string kind, double elapsedMs)
    {
        try
        {
            await _metrics
                .RecordAsync(new MetricEntry(sessionId, kind, elapsedMs, null, null))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "指标记录失败：{Kind}", kind);
        }
    }

    /// <summary>记一次检索命中（命中条数放 InputTokens 列，用于算命中率）。</summary>
    private async Task RecordHitAsync(string sessionId, int hits, CancellationToken ct)
    {
        try
        {
            await _metrics
                .RecordAsync(new MetricEntry(sessionId, "retrieval.hit", ElapsedMs: 0, InputTokens: hits), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "指标记录失败：retrieval.hit");
        }
    }
}
