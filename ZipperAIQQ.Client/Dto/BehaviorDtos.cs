using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

/// <summary>行为设置（<c>getBehavior</c>）：回复模式 + 通知响应开关（S3-D 起按平台）。</summary>
public sealed class BehaviorDto
{
    [JsonPropertyName("replyMode")]
    public string ReplyMode { get; set; } = "every";

    [JsonPropertyName("replyModes")]
    public List<string> ReplyModes { get; set; } = [];

    [JsonPropertyName("notices")]
    public List<NoticeToggleDto> Notices { get; set; } = [];

    /// <summary>
    /// 可切换的平台列表（如 <c>["qq"]</c>，S3-D / 决策六）。**旧服务端不返回 → 空列表**，
    /// 客户端退化成只有请求时带的那个平台（缺省 <c>qq</c>），不报错。
    /// </summary>
    [JsonPropertyName("platforms")]
    public List<string> Platforms { get; set; } = [];
}

public sealed class NoticeToggleDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// 这条通知属于哪个平台（S3-D / 决策六，如 <c>"qq"</c>）。
    /// **旧服务端不返回 → 缺省 <c>qq</c>**（现有通知类型全是 QQ 语义），不报错。
    /// </summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "qq";

    /// <summary>common / other（#34：常用单独一组）。</summary>
    [JsonPropertyName("group")]
    public string Group { get; set; } = "other";

    [JsonPropertyName("groupLabel")]
    public string GroupLabel { get; set; } = "其他";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>工具 / 命令 / NapCat API 策略项（#28）。</summary>
public sealed class ToolCatalogItemDto
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("categoryLabel")]
    public string CategoryLabel { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("role")]
    public string Role { get; set; } = "Normal";

    [JsonPropertyName("defaultRole")]
    public string DefaultRole { get; set; } = "Normal";

    [JsonPropertyName("hardFloor")]
    public string? HardFloor { get; set; }

    /// <summary>
    /// 该能力适用的平台码（S3-D / 决策五，如 <c>["qq"]</c> 表示 QQ 专用、<c>["qq","douyin"]</c> 表示通用）。
    /// **空表 = 通用**：旧服务端不返回 <c>platforms</c> 时就是空表，界面归到「通用」组，不报错。
    /// </summary>
    [JsonPropertyName("platforms")]
    public List<string> Platforms { get; set; } = [];
}

/// <summary>人设（<c>getPersona</c>）：形象 / 性格分开。</summary>
public sealed class PersonaDto
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("personality")]
    public string? Personality { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("split")]
    public bool Split { get; set; }

    /// <summary>会话级预设是否启用（#16）。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>该会话是否有自己的预设（没有时开关禁用）。</summary>
    [JsonPropertyName("hasOverride")]
    public bool HasOverride { get; set; }

    [JsonPropertyName("updated")]
    public int Updated { get; set; }

    [JsonPropertyName("skipped")]
    public bool Skipped { get; set; }
}

/// <summary>系统提示词（与人设分开）。</summary>
public sealed class SystemPromptDto
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("defaultText")]
    public string DefaultText { get; set; } = string.Empty;
}
