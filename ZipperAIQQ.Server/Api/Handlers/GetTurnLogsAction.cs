using System.Text.Json;
using ZipperAIQQ.Storage.Turns;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>最近的轮次审计（K-5：静默收轮的唯一产物，供诊断与后台查看）。</summary>
public sealed class GetTurnLogsAction : IApiAction
{
    private readonly ITurnLogStore _logs;

    public GetTurnLogsAction(ITurnLogStore logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        _logs = logs;
    }

    public string Name => "getTurnLogs";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var limit = payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("limit", out var raw) &&
                    raw.TryGetInt32(out var parsed) && parsed > 0
            ? Math.Min(parsed, 200)
            : 20;

        var rows = await _logs.ListRecentAsync(limit, ct).ConfigureAwait(false);

        return new
        {
            turns = rows.Select(r => new
            {
                sessionId = r.SessionId,
                startedAt = r.StartedAt,
                endedAt = r.EndedAt,
                endReason = r.EndReason,
                steerCount = r.SteerCount,
                followUpCount = r.FollowUpCount,
            }).ToList(),
        };
    }
}
