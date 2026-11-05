using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

/// <summary><c>getAlerts</c> 的响应。</summary>
public sealed class AlertsResponseDto
{
    [JsonPropertyName("alerts")]
    public List<AlertItemDto> Alerts { get; set; } = [];
}

/// <summary>一条告警投递记录。</summary>
public sealed class AlertItemDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>类型 + 平台（列表主行）。</summary>
    [JsonIgnore]
    public string Summary =>
        string.IsNullOrEmpty(Platform) ? Kind : $"{Kind} · {Platform}";

    /// <summary>送达状态（次行）。</summary>
    [JsonIgnore]
    public string DetailText => Ok ? "已送达" : $"失败：{Error ?? "未知原因"}";

    [JsonIgnore]
    public string TimeText => At.ToLocalTime().ToString("MM-dd HH:mm");
}
