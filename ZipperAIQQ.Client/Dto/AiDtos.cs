namespace ZipperAIQQ.Client.Dto;

/// <summary>对话增强开关（<c>getDialogEnhance</c> / <c>setDialogEnhance</c>）。</summary>
public sealed class DialogEnhanceDto
{
    [System.Text.Json.Serialization.JsonPropertyName("webSearch")]
    public bool WebSearch { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("readUrl")]
    public bool ReadUrl { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("safetyCheck")]
    public bool SafetyCheck { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public bool Title { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("anyEnabled")]
    public bool AnyEnabled { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>服务端给的副作用说明（开启后采样参数被服务端接管）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("updated")]
    public bool Updated { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("skipped")]
    public bool Skipped { get; set; }

    /// <summary>保存后是否**真的**生效（<c>false</c> = 已存库但 LLM 客户端重建失败）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("effective")]
    public bool Effective { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>一条全家桶用量（<c>getToolUsage</c>）：按计费单位聚合，与 token 口径分开。</summary>
public sealed class ToolUsageDto
{
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("kindLabel")]
    public string KindLabel { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("unitLabel")]
    public string UnitLabel { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("calls")]
    public long Calls { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("total")]
    public double Total { get; set; }

    public string Display => $"{KindLabel}：{Calls} 次 / {Total:0.#} {UnitLabel}";
}
