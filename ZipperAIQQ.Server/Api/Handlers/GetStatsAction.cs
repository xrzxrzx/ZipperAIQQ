using System.Text.Json;
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
        var llm = Find(rows, "llm.call");
        var retry = Find(rows, "llm.retry");
        var retrieval = Find(rows, "retrieval");
        var hit = Find(rows, "retrieval.hit");
        var jev = Find(rows, "jev.judge");
        var degrade = Find(rows, "jev.degrade");
        var summary = Find(rows, "summary");
        var sendMessage = Find(rows, "tool.send_message");
        var embedding = Find(rows, "embedding.backfill");

        var retrievalCount = retrieval?.Count ?? 0;
        var hitMessages = hit?.TotalInputTokens ?? 0;

        // 命中率：有命中的检索次数 / 检索总次数（不是"命中条数 / 次数"，口径更准）
        var hitRate = retrievalCount > 0
            ? Math.Round((double)(hit?.Count ?? 0) / retrievalCount, 4)
            : 0;

        var jevCount = jev?.Count ?? 0;
        var jevReplies = jev?.TotalInputTokens ?? 0;

        var inputTokens = llm?.TotalInputTokens ?? 0;
        var cachedTokens = llm?.TotalCachedInputTokens ?? 0;
        var cacheHitRate = inputTokens > 0
            ? Math.Round((double)cachedTokens / inputTokens, 4)
            : 0;

        return new
        {
            llm = new
            {
                calls = llm?.Count ?? 0,
                inputTokens,
                outputTokens = llm?.TotalOutputTokens ?? 0,
                cachedInputTokens = cachedTokens,
                cacheHitRate,
                avgMs = Math.Round(llm?.AvgMs ?? 0, 1),
                maxMs = Math.Round(llm?.MaxMs ?? 0, 1),
                retries = retry?.Count ?? 0,
            },
            retrieval = new
            {
                count = retrievalCount,
                hitCount = hit?.Count ?? 0,
                hitMessages,
                hitRate,
                avgHitsPerHit = hit is not null && hit.Count > 0
                    ? Math.Round((double)hitMessages / hit.Count, 2)
                    : 0,
                avgMs = Math.Round(retrieval?.AvgMs ?? 0, 1),
            },
            jev = new
            {
                count = jevCount,
                replies = jevReplies,
                silences = Math.Max(0, jevCount - jevReplies),
                replyRate = jevCount > 0 ? Math.Round((double)jevReplies / jevCount, 4) : 0,
                degradeCount = degrade?.Count ?? 0,
                avgMs = Math.Round(jev?.AvgMs ?? 0, 1),
            },
            summary = new { count = summary?.Count ?? 0 },
            tools = new
            {
                sendMessage = sendMessage?.Count ?? 0,
                embeddingBackfill = embedding?.Count ?? 0,
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
