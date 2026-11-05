using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Server.Metrics;

/// <summary>指标 kind 常量（写入端与展示端共用，避免三处各写一份字符串）。</summary>
public static class MetricKinds
{
    public const string LlmCall = "llm.call";
    public const string LlmRetry = "llm.retry";

    /// <summary>模型调用失败（含分类原因写日志；指标只记次数）。</summary>
    public const string LlmFail = "llm.fail";
    public const string Retrieval = "retrieval";
    public const string RetrievalHit = "retrieval.hit";
    public const string JevJudge = "jev.judge";
    public const string JevDegrade = "jev.degrade";
    public const string Summary = "summary";
    public const string SendMessage = "tool.send_message";
    public const string EmbeddingBackfill = "embedding.backfill";

    /// <summary>
    /// 一次**完整轮次**的耗时（含工具循环与漂移重试），与 <see cref="LlmCall"/> 的单次往返区分开。
    /// </summary>
    /// <remarks>
    /// 用户看到的等待时间更像这一条；而「模型算得快不快」要看 <see cref="LlmCall"/>。
    /// 两者混在一列里，就会出现「平均上万毫秒，但这其实是含排队的整轮」这种误读。
    /// </remarks>
    public const string TurnTotal = "turn.total";
}

/// <summary>从 <see cref="MetricSummary"/> 行算出的一批展示/判断口径。</summary>
/// <remarks>
/// 以前 <c>/status</c> 与 <c>getStats</c> 各算一遍命中率、JEV 回/不回、降级次数，
/// 公式漂移时两边会给出不同数字。这里只保留一份算法，展示层只做格式化。
/// </remarks>
public sealed record MetricFacts(
    long LlmCalls,
    double LlmAvgMs,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long LlmRetries,
    long RetrievalCount,
    long RetrievalHits,
    long HitMessages,
    double HitRate,
    long JevCount,
    long JevReplies,
    long JevDegrade,
    long SummaryCount,
    long SendMessage,
    long EmbeddingBackfill,
    double LlmP95Ms = 0,
    long TurnCalls = 0,
    double TurnAvgMs = 0,
    double TurnP95Ms = 0)
{
    public static MetricFacts From(IReadOnlyList<MetricSummary> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var llm = Find(rows, MetricKinds.LlmCall);
        var retry = Find(rows, MetricKinds.LlmRetry);
        var retrieval = Find(rows, MetricKinds.Retrieval);
        var hit = Find(rows, MetricKinds.RetrievalHit);
        var jev = Find(rows, MetricKinds.JevJudge);
        var degrade = Find(rows, MetricKinds.JevDegrade);
        var summary = Find(rows, MetricKinds.Summary);
        var sendMessage = Find(rows, MetricKinds.SendMessage);
        var embedding = Find(rows, MetricKinds.EmbeddingBackfill);
        var turn = Find(rows, MetricKinds.TurnTotal);

        var retrievalCount = retrieval?.Count ?? 0;
        var hitMessages = hit?.TotalInputTokens ?? 0;

        // 命中率：有命中的检索次数 / 检索总次数（不是"命中条数 / 次数"，口径更准）
        var hitRate = retrievalCount > 0
            ? Math.Round((double)(hit?.Count ?? 0) / retrievalCount, 4)
            : 0;

        return new MetricFacts(
            LlmCalls: llm?.Count ?? 0,
            LlmAvgMs: llm?.AvgMs ?? 0,
            InputTokens: llm?.TotalInputTokens ?? 0,
            OutputTokens: llm?.TotalOutputTokens ?? 0,
            CachedInputTokens: llm?.TotalCachedInputTokens ?? 0,
            LlmRetries: retry?.Count ?? 0,
            RetrievalCount: retrievalCount,
            RetrievalHits: hit?.Count ?? 0,
            HitMessages: hitMessages,
            HitRate: hitRate,
            JevCount: jev?.Count ?? 0,
            JevReplies: jev?.TotalInputTokens ?? 0,
            JevDegrade: degrade?.Count ?? 0,
            SummaryCount: summary?.Count ?? 0,
            SendMessage: sendMessage?.Count ?? 0,
            EmbeddingBackfill: embedding?.Count ?? 0,
            LlmP95Ms: llm?.P95Ms ?? 0,
            TurnCalls: turn?.Count ?? 0,
            TurnAvgMs: turn?.AvgMs ?? 0,
            TurnP95Ms: turn?.P95Ms ?? 0);
    }

    private static MetricSummary? Find(IReadOnlyList<MetricSummary> rows, string kind) =>
        rows.FirstOrDefault(m => string.Equals(m.Kind, kind, StringComparison.Ordinal));
}
