using System.Text.Json;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Logs;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>按时间 / 级别 / 关键词查服务端日志（问题记录 #31）。</summary>
public sealed class QueryLogsAction : IApiAction
{
    private readonly ILogQuery _logs;

    public QueryLogsAction(ILogQuery logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        _logs = logs;
    }

    public string Name => "queryLogs";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var from = ReadTime(payload, "from");
        var to = ReadTime(payload, "to");

        if (from is not null && to is not null && from > to)
        {
            throw new ApiException(ApiResponse.BadRequest, "from 不能晚于 to。");
        }

        if (from is not null && to is not null && to - from > TimeSpan.FromDays(31))
        {
            throw new ApiException(ApiResponse.BadRequest, "时间跨度不能超过 31 天。");
        }

        var limit = Math.Clamp(PayloadReader.GetInt(payload, "limit", 200), 1, 500);
        var offset = Math.Max(0, PayloadReader.GetInt(payload, "offset", 0));

        var rows = await _logs.QueryAsync(new LogQuery(
            from,
            to,
            PayloadReader.GetString(payload, "level"),
            PayloadReader.GetString(payload, "keyword"),
            limit,
            offset), ct).ConfigureAwait(false);

        return rows
            .Select(r => new
            {
                at = r.At.ToUnixTimeMilliseconds(),
                level = r.Level,
                message = r.Message,
                file = r.File,
            })
            .ToList();
    }

    private static DateTimeOffset? ReadTime(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var ms))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        throw new ApiException(ApiResponse.BadRequest, $"{name} 必须是 Unix 毫秒。");
    }
}
