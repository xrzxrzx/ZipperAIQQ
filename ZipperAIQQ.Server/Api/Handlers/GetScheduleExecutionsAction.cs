using System.Text.Json;
using ZipperAIQQ.Storage.Schedules;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>最近定时任务执行记录（G-6：谁决定发/不发/失败）。</summary>
public sealed class GetScheduleExecutionsAction : IApiAction
{
    private readonly IScheduleExecutionStore _executions;

    public GetScheduleExecutionsAction(IScheduleExecutionStore executions)
    {
        ArgumentNullException.ThrowIfNull(executions);
        _executions = executions;
    }

    public string Name => "getScheduleExecutions";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var limit = payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("limit", out var raw) &&
                    raw.TryGetInt32(out var parsed) && parsed > 0
            ? Math.Min(parsed, 100)
            : 10;

        var rows = await _executions.ListRecentAsync(limit, ct).ConfigureAwait(false);

        return new
        {
            executions = rows.Select(e => new
            {
                taskId = e.TaskId,
                outcome = e.Outcome,
                detail = e.Detail,
                at = e.CreatedAt,
            }).ToList(),
        };
    }
}
