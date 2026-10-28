using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

/// <summary>一条服务端日志（#31）。</summary>
public sealed class LogLineDto
{
    [JsonPropertyName("at")]
    public long At { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonIgnore]
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(At).LocalDateTime.ToString("MM-dd HH:mm:ss");

    [JsonIgnore]
    public bool IsError => Level is "ERR" or "FTL";

    [JsonIgnore]
    public bool IsWarning => Level == "WRN";

    [JsonIgnore]
    public bool IsInfo => !IsError && !IsWarning;

    [JsonIgnore]
    public string Display => $"[{TimeText} {Level}] {Message}";
}

/// <summary>网页搜索配置（与模型端点独立）。</summary>
public sealed class SearchDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("hasKey")]
    public bool HasKey { get; set; }
}
