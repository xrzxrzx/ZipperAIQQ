using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

/// <summary>端点配置（<c>getConfig</c>，key 只回掩码）。</summary>
public sealed class EndpointDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("apiKind")]
    public int ApiKind { get; set; }

    [JsonPropertyName("keyMasked")]
    public string KeyMasked { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }
}

/// <summary>配置视图（<c>getConfig</c>）。</summary>
public sealed class ConfigDto
{
    [JsonPropertyName("endpoints")]
    public List<EndpointDto> Endpoints { get; set; } = [];

    [JsonPropertyName("roles")]
    public Dictionary<string, string> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("hasClient")]
    public bool HasClient { get; set; }

    /// <summary>固定端点名（#12：界面只读展示 + 下拉）。</summary>
    [JsonPropertyName("endpointNames")]
    public List<string> EndpointNames { get; set; } = [];

    public string Summary
    {
        get
        {
            if (Endpoints.Count == 0)
            {
                return "还没有配置端点。";
            }

            var lines = Endpoints.Select(e =>
                $"- {e.Name}：{e.Model} @ {e.BaseUrl}（key {e.KeyMasked}，apiKind {e.ApiKind}）");
            var roles = string.Join("，", Roles.Select(r => $"{r.Key}→{r.Value}"));
            return string.Join('\n', lines) + $"\n\n角色映射：{roles}\n客户端就绪：{(HasClient ? "是" : "否")}";
        }
    }
}

/// <summary>审计条目（<c>getAudit</c>）。</summary>
public sealed class AuditDto
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("actor")]
    public string Actor { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("at")]
    public long At { get; set; }

    public string Display =>
        $"[{DateTimeOffset.FromUnixTimeMilliseconds(At).LocalDateTime:MM-dd HH:mm:ss}] {Action} by {Actor} → {Target}";
}

/// <summary>契约统一的 JSON 选项（Web：camelCase）。</summary>
public static class ClientJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>一条数值型运行参数（<c>getSettings</c>）。</summary>
public sealed class SettingDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("max")]
    public double Max { get; set; }

    [JsonPropertyName("default")]
    public double Default { get; set; }

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("stored")]
    public bool Stored { get; set; }
}
