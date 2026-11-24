using System.Text.Json;
using ZipperAIQQ.Storage.Ai;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 全家桶用量（问题记录 06 T2.7）：按**计费单位**聚合，与 token 口径分开展示。
/// </summary>
/// <remarks>
/// 与 <c>getMetrics</c> 并列而不是合并：那张表是 token / 耗时，
/// 混进「页 / 字符 / 秒」会把「token 用量」这张卡整体算错。
/// </remarks>
public sealed class GetToolUsageAction : IApiAction
{
    private readonly IToolCallStore _store;

    public GetToolUsageAction(IToolCallStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public string Name => "getToolUsage";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var windowHours = PayloadReader.GetInt(payload, "windowHours", 24);
        var sessionId = PayloadReader.GetString(payload, "sessionId");

        var summary = await _store
            .GetSummaryAsync(windowHours, string.IsNullOrWhiteSpace(sessionId) ? null : sessionId, ct)
            .ConfigureAwait(false);

        return summary
            .Select(s => new
            {
                kind = s.Kind,
                kindLabel = MetricKindCatalog.ToolKindLabel(s.Kind),
                unit = s.Unit,
                unitLabel = MetricKindCatalog.ToolUnitLabel(s.Unit),
                calls = s.Calls,
                // 页 / 字符 / 秒都是整数语义；保留一位小数只是为了让「秒」别被抹平
                total = Math.Round(s.TotalAmount, 1),
            })
            .ToList();
    }
}
