using System.Text.Json;
using ZipperAIQQ.Server.Metrics;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 数据页统计（M2-E）：Token 用量（含缓存命中）/ 检索命中率 / JEV 判断 / 压缩次数。
/// </summary>
/// <remarks>
/// <para>
/// 数据全部来自 <c>CallMetrics</c> 的聚合，**不读消息正文**。
/// </para>
/// <para>
/// 两个口径：<c>last24h</c> 给趋势看，<c>allTime</c> 给总量看 —— 用户看到的"烧了多少"通常是总量。
/// </para>
/// <para>
/// payload 可带 <c>sessionId</c>（#5）：只看某个会话。不带 = 全部会话。
/// 同时返回 <c>sessions</c>（有指标的会话列表），供页面上的下拉选择。
/// </para>
/// </remarks>
public sealed class GetStatsAction : IApiAction
{
    private readonly IMetricsStore _metrics;

    public GetStatsAction(IMetricsStore metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    public string Name => "getStats";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var sessionId = payload.ValueKind == JsonValueKind.Object &&
                        payload.TryGetProperty("sessionId", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            sessionId = null;
        }

        var day = await _metrics.GetSummaryAsync(24, sessionId, ct).ConfigureAwait(false);
        var all = await _metrics.GetSummaryAsync(0, sessionId, ct).ConfigureAwait(false);
        var sessions = await _metrics.ListSessionAsync(24, ct).ConfigureAwait(false);

        return new
        {
            windowHours = 24,
            sessionId,
            sessions = sessions
                .Select(s => new
                {
                    sessionId = s.SessionId,
                    calls = s.Calls,
                    lastAt = s.LastAt.ToUnixTimeMilliseconds(),
                })
                .ToList(),
            last24h = Build(day),
            allTime = Build(all),
        };
    }

    private static object Build(IReadOnlyList<MetricSummary> rows)
    {
        var facts = MetricFacts.From(rows);
        var cacheHitRate = facts.InputTokens > 0
            ? Math.Round((double)facts.CachedInputTokens / facts.InputTokens, 4)
            : 0;

        var llm = Find(rows, MetricKinds.LlmCall);
        var retrieval = Find(rows, MetricKinds.Retrieval);
        var jev = Find(rows, MetricKinds.JevJudge);

        return new
        {
            llm = new
            {
                calls = facts.LlmCalls,
                inputTokens = facts.InputTokens,
                outputTokens = facts.OutputTokens,
                cachedInputTokens = facts.CachedInputTokens,
                cacheHitRate,
                avgMs = Math.Round(facts.LlmAvgMs, 1),
                maxMs = Math.Round(llm?.MaxMs ?? 0, 1),
                retries = facts.LlmRetries,
            },
            retrieval = new
            {
                count = facts.RetrievalCount,
                hitCount = facts.RetrievalHits,
                hitMessages = facts.HitMessages,
                hitRate = facts.HitRate,
                avgHitsPerHit = facts.RetrievalHits > 0
                    ? Math.Round((double)facts.HitMessages / facts.RetrievalHits, 2)
                    : 0,
                avgMs = Math.Round(retrieval?.AvgMs ?? 0, 1),
            },
            jev = new
            {
                count = facts.JevCount,
                replies = facts.JevReplies,
                silences = Math.Max(0, facts.JevCount - facts.JevReplies),
                replyRate = facts.JevCount > 0
                    ? Math.Round((double)facts.JevReplies / facts.JevCount, 4)
                    : 0,
                degradeCount = facts.JevDegrade,
                avgMs = Math.Round(jev?.AvgMs ?? 0, 1),
            },
            summary = new { count = facts.SummaryCount },
            tools = new
            {
                sendMessage = facts.SendMessage,
                embeddingBackfill = facts.EmbeddingBackfill,
            },
            kinds = rows
                .OrderByDescending(r => r.Count)
                .Select(r => new
                {
                    kind = r.Kind,
                    kindLabel = MetricKindCatalog.Label(r.Kind),
                    count = r.Count,
                    avgMs = Math.Round(r.AvgMs, 1),
                    minMs = Math.Round(r.MinMs, 1),
                    maxMs = Math.Round(r.MaxMs, 1),
                    inputTokens = r.TotalInputTokens,
                    outputTokens = r.TotalOutputTokens,
                    cachedInputTokens = r.TotalCachedInputTokens,
                })
                .ToList(),
        };
    }

    private static MetricSummary? Find(IReadOnlyList<MetricSummary> rows, string kind) =>
        rows.FirstOrDefault(r => string.Equals(r.Kind, kind, StringComparison.Ordinal));
}
