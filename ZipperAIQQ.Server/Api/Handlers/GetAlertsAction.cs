using System.Text.Json;
using ZipperAIQQ.Storage.Alerts;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>最近告警（供客户端展示「出过什么事」）。</summary>
public sealed class GetAlertsAction : IApiAction
{
    private readonly IAlertStore _alerts;

    public GetAlertsAction(IAlertStore alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        _alerts = alerts;
    }

    public string Name => "getAlerts";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var limit = payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("limit", out var raw) &&
                    raw.TryGetInt32(out var parsed) && parsed > 0
            ? Math.Min(parsed, 200)
            : 20;

        var rows = await _alerts.ListRecentAsync(limit, ct).ConfigureAwait(false);

        return new
        {
            alerts = rows.Select(a => new
            {
                kind = a.Kind,
                platform = a.Platform,
                target = a.Target,
                ok = a.Ok,
                error = a.Error,
                at = a.CreatedAt,
            }).ToList(),
        };
    }
}
