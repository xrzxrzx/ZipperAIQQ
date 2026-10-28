using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

/// <summary>反向 WS 监听块（问题记录 03 #5）。</summary>
public sealed class ReverseServerDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("tokenConfigured")]
    public bool TokenConfigured { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>正向 WS 目标块。</summary>
public sealed class ForwardServerDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("tokenConfigured")]
    public bool TokenConfigured { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

/// <summary>外部 WS 配置（问题记录 03 #5）。</summary>
public sealed class ExternalDto
{
    [JsonPropertyName("allowSend")]
    public bool AllowSend { get; set; }

    [JsonPropertyName("allowedSessions")]
    public string[] AllowedSessions { get; set; } = [];

    [JsonPropertyName("reverseServers")]
    public ReverseServerDto[] ReverseServers { get; set; } = [];

    [JsonPropertyName("forwardServers")]
    public ForwardServerDto[] ForwardServers { get; set; } = [];
}

/// <summary>主人反馈设置（问题记录 03 #4）。</summary>
public sealed class FeedbackDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("types")]
    public string[] Types { get; set; } = [];

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;
}

/// <summary>保存类 action 的统一结果（#10 幂等）。</summary>
public sealed class SaveResultDto
{
    [JsonPropertyName("updated")]
    public int Updated { get; set; }

    [JsonPropertyName("skipped")]
    public bool Skipped { get; set; }

    [JsonPropertyName("changed")]
    public List<string> Changed { get; set; } = [];
}
