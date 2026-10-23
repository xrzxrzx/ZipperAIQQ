using System.Text.Json;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>调用指标（FR-10）：按类别聚合，按平均耗时降序。</summary>
public sealed class GetMetricsAction : IApiAction
{
    private readonly IMetricsStore _metrics;

    public GetMetricsAction(IMetricsStore metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    public string Name => "getMetrics";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        // 0 或负数 = 不限时间窗（与 IMetricsStore 语义一致）
        var windowHours = PayloadReader.GetInt(payload, "windowHours", 24);
        var sessionId = payload.TryGetProperty("sessionId", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;

        var summary = await _metrics
            .GetSummaryAsync(windowHours, string.IsNullOrWhiteSpace(sessionId) ? null : sessionId, ct)
            .ConfigureAwait(false);

        return summary
            .Select(m => new
            {
                kind = m.Kind,
                kindLabel = MetricKindCatalog.Label(m.Kind),
                count = m.Count,
                avgMs = Math.Round(m.AvgMs, 1),
                minMs = Math.Round(m.MinMs, 1),
                maxMs = Math.Round(m.MaxMs, 1),
                totalInputTokens = m.TotalInputTokens,
                totalOutputTokens = m.TotalOutputTokens,
                totalCachedInputTokens = m.TotalCachedInputTokens,
            })
            .ToList();
    }
}
