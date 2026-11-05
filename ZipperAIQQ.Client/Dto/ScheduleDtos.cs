using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

/// <summary><c>getScheduleExecutions</c> 响应。</summary>
public sealed class ScheduleExecutionsResponseDto
{
    [JsonPropertyName("executions")]
    public List<ScheduleExecutionDto> Executions { get; set; } = [];
}

/// <summary>一条定时任务执行记录。</summary>
public sealed class ScheduleExecutionDto
{
    [JsonPropertyName("taskId")]
    public long TaskId { get; set; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    [JsonIgnore]
    public string OutcomeText => Outcome switch
    {
        "Sent" => "已发送",
        "SkippedByAgent" => "Agent 决定不发",
        "TimedOutDefaultSent" => "超时兜底发送",
        "Failed" => "失败",
        _ => Outcome,
    };

    [JsonIgnore]
    public string Summary => $"#{TaskId} · {OutcomeText}";

    [JsonIgnore]
    public string DetailText => string.IsNullOrWhiteSpace(Detail) ? "—" : Detail!;

    [JsonIgnore]
    public string TimeText => At.ToLocalTime().ToString("MM-dd HH:mm");
}
