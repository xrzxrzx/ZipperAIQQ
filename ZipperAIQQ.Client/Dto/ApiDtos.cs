using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

/// <summary>命令响应：<c>{ code, message, data }</c>（与 <c>docs/05-契约</c> 一致）。</summary>
public sealed class ApiResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

/// <summary>连通性检查（<c>ping</c>）。</summary>
public sealed class PingDto
{
    [JsonPropertyName("serverTime")]
    public long ServerTime { get; set; }

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; } = string.Empty;
}

/// <summary>服务端状态（<c>getStatus</c>）。</summary>
public sealed class StatusDto
{
    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; set; }

    [JsonPropertyName("channelConnected")]
    public bool ChannelConnected { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("sessions")]
    public int Sessions { get; set; }

    [JsonPropertyName("pendingTasks")]
    public int PendingTasks { get; set; }

    [JsonPropertyName("httpAddress")]
    public string HttpAddress { get; set; } = string.Empty;
}

/// <summary>会话概览（<c>listSessions</c>）。</summary>
public sealed class SessionDto
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; set; }

    [JsonPropertyName("lastMessageAt")]
    public long LastMessageAt { get; set; }

    public string LastMessageText =>
        DateTimeOffset.FromUnixTimeMilliseconds(LastMessageAt).LocalDateTime.ToString("MM-dd HH:mm");
}

/// <summary>一条消息（<c>getMessages</c>）。</summary>
public sealed class MessageDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("isOutgoing")]
    public bool IsOutgoing { get; set; }

    [JsonPropertyName("sender")]
    public string Sender { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("occurredAt")]
    public long OccurredAt { get; set; }

    public string TimeText =>
        DateTimeOffset.FromUnixTimeMilliseconds(OccurredAt).LocalDateTime.ToString("HH:mm:ss");

    public string Display => $"[{TimeText}] {(IsOutgoing ? "AI" : Sender)}：{Text}";
}

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

/// <summary>保存运行参数的结果（<c>setSettings</c>）。</summary>
public sealed class SetSettingsResultDto
{
    [JsonPropertyName("updated")]
    public int Updated { get; set; }

    [JsonPropertyName("keys")]
    public List<string> Keys { get; set; } = [];
}

/// <summary>统计数据（<c>getStats</c>）：24 小时窗口 + 全量。</summary>
public sealed class StatsDto
{
    [JsonPropertyName("windowHours")]
    public int WindowHours { get; set; }

    [JsonPropertyName("last24h")]
    public StatsWindowDto Last24h { get; set; } = new();

    [JsonPropertyName("allTime")]
    public StatsWindowDto AllTime { get; set; } = new();
}

public sealed class StatsWindowDto
{
    [JsonPropertyName("llm")]
    public LlmStatsDto Llm { get; set; } = new();

    [JsonPropertyName("retrieval")]
    public RetrievalStatsDto Retrieval { get; set; } = new();

    [JsonPropertyName("jev")]
    public JevStatsDto Jev { get; set; } = new();

    [JsonPropertyName("summary")]
    public SummaryStatsDto Summary { get; set; } = new();

    [JsonPropertyName("tools")]
    public ToolStatsDto Tools { get; set; } = new();

    [JsonPropertyName("kinds")]
    public List<KindStatsDto> Kinds { get; set; } = [];
}

public sealed class LlmStatsDto
{
    [JsonPropertyName("calls")]
    public long Calls { get; set; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; set; }

    [JsonPropertyName("avgMs")]
    public double AvgMs { get; set; }

    [JsonPropertyName("maxMs")]
    public double MaxMs { get; set; }

    [JsonPropertyName("retries")]
    public long Retries { get; set; }
}

public sealed class RetrievalStatsDto
{
    [JsonPropertyName("count")]
    public long Count { get; set; }

    [JsonPropertyName("hitCount")]
    public long HitCount { get; set; }

    [JsonPropertyName("hitMessages")]
    public long HitMessages { get; set; }

    [JsonPropertyName("hitRate")]
    public double HitRate { get; set; }

    [JsonPropertyName("avgHitsPerHit")]
    public double AvgHitsPerHit { get; set; }

    [JsonPropertyName("avgMs")]
    public double AvgMs { get; set; }
}

public sealed class JevStatsDto
{
    [JsonPropertyName("count")]
    public long Count { get; set; }

    [JsonPropertyName("replies")]
    public long Replies { get; set; }

    [JsonPropertyName("silences")]
    public long Silences { get; set; }

    [JsonPropertyName("replyRate")]
    public double ReplyRate { get; set; }

    [JsonPropertyName("degradeCount")]
    public long DegradeCount { get; set; }

    [JsonPropertyName("avgMs")]
    public double AvgMs { get; set; }
}

public sealed class SummaryStatsDto
{
    [JsonPropertyName("count")]
    public long Count { get; set; }
}

public sealed class ToolStatsDto
{
    [JsonPropertyName("sendMessage")]
    public long SendMessage { get; set; }

    [JsonPropertyName("embeddingBackfill")]
    public long EmbeddingBackfill { get; set; }
}

public sealed class KindStatsDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public long Count { get; set; }

    [JsonPropertyName("avgMs")]
    public double AvgMs { get; set; }

    [JsonPropertyName("minMs")]
    public double MinMs { get; set; }

    [JsonPropertyName("maxMs")]
    public double MaxMs { get; set; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; set; }

    /// <summary>给界面直接用（x:Bind 不做数值→字符串转换）。</summary>
    [JsonIgnore]
    public string CountText => Count.ToString("N0");

    [JsonIgnore]
    public string AvgMsText => $"{AvgMs:F0} ms";

    [JsonIgnore]
    public string TokenText => InputTokens == 0 && OutputTokens == 0
        ? "—"
        : $"{InputTokens:N0} / {OutputTokens:N0}";
}

/// <summary>行为设置（<c>getBehavior</c>）：回复模式 + 通知响应开关。</summary>
public sealed class BehaviorDto
{
    [JsonPropertyName("replyMode")]
    public string ReplyMode { get; set; } = "every";

    [JsonPropertyName("replyModes")]
    public List<string> ReplyModes { get; set; } = [];

    [JsonPropertyName("notices")]
    public List<NoticeToggleDto> Notices { get; set; } = [];
}

public sealed class NoticeToggleDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
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
}
