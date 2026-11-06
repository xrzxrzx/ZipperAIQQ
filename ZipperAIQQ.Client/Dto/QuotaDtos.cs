using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

/// <summary><c>getQuota</c> 响应。</summary>
public sealed class QuotaDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("window")]
    public string Window { get; set; } = "day";

    [JsonPropertyName("limitCny")]
    public decimal LimitCny { get; set; }

    [JsonPropertyName("warnRatio")]
    public decimal WarnRatio { get; set; }

    [JsonPropertyName("spentCny")]
    public decimal SpentCny { get; set; }

    /// <summary>balance / estimate / none。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "none";

    [JsonPropertyName("remainingCny")]
    public decimal? RemainingCny { get; set; }

    [JsonPropertyName("hasData")]
    public bool HasData { get; set; }
}

/// <summary><c>setQuota</c> 响应。</summary>
public sealed class QuotaSavedDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("window")]
    public string Window { get; set; } = "day";

    [JsonPropertyName("limitCny")]
    public decimal LimitCny { get; set; }

    [JsonPropertyName("warnRatio")]
    public decimal WarnRatio { get; set; }
}
