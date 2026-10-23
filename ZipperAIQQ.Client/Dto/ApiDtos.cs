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

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    /// <summary>0 = 私聊，1 = 群聊（与服务端 ChatType 一致）。</summary>
    [JsonPropertyName("chatType")]
    public int ChatType { get; set; }

    /// <summary>「QQ · 昵称」/「QQ群 · 群名」；解析失败时等于 SessionId。</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; set; }

    [JsonPropertyName("lastMessageAt")]
    public long LastMessageAt { get; set; }

    /// <summary>列表主标题：优先显示名，旧服务端没给就退回编号。</summary>
    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? SessionId : DisplayName;

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

    /// <summary>发送人显示名（AI 统一显示「AI」）。</summary>
    [JsonIgnore]
    public string SenderText => IsOutgoing ? "AI" : Sender;

    /// <summary>正文（不含时间 / 发送人，便于分段高亮）。</summary>
    [JsonIgnore]
    public string BodyText => Text;

    /// <summary>是否用户侧（给 x:Load 做二选一显示）。</summary>
    [JsonIgnore]
    public bool IsUser => !IsOutgoing;

    public string Display => $"[{TimeText}] {SenderText}：{Text}";
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

    /// <summary>本次统计的会话口径；<c>null</c> = 全部会话。</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>有指标记录的会话（下拉用）。</summary>
    [JsonPropertyName("sessions")]
    public List<SessionStatsDto> Sessions { get; set; } = [];

    [JsonPropertyName("last24h")]
    public StatsWindowDto Last24h { get; set; } = new();

    [JsonPropertyName("allTime")]
    public StatsWindowDto AllTime { get; set; } = new();
}

/// <summary>一个会话的指标概况（数据页筛选下拉）。</summary>
public sealed class SessionStatsDto
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("calls")]
    public long Calls { get; set; }

    [JsonPropertyName("lastAt")]
    public long LastAt { get; set; }

    /// <summary>下拉显示用（B3 起会换成昵称）。</summary>
    [JsonIgnore]
    public string Display => SessionId;
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

    /// <summary>命中缓存的输入 token。</summary>
    [JsonPropertyName("cachedInputTokens")]
    public long CachedInputTokens { get; set; }

    /// <summary>缓存命中率（0~1）。</summary>
    [JsonPropertyName("cacheHitRate")]
    public double CacheHitRate { get; set; }

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

    /// <summary>中文名（#6：界面主显示；英文仍保留在 <see cref="Kind"/>）。</summary>
    [JsonPropertyName("kindLabel")]
    public string KindLabel { get; set; } = string.Empty;

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

    [JsonPropertyName("cachedInputTokens")]
    public long CachedInputTokens { get; set; }

    /// <summary>给界面直接用（x:Bind 不做数值→字符串转换）。</summary>
    [JsonIgnore]
    public string KindTitle => string.IsNullOrWhiteSpace(KindLabel) ? Kind : KindLabel;

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

/// <summary>人员与名单（<c>getAccess</c>）。</summary>
public sealed class AccessDto
{
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; set; }

    [JsonPropertyName("admins")]
    public List<string> Admins { get; set; } = [];

    [JsonPropertyName("filterMode")]
    public string FilterMode { get; set; } = "Blacklist";

    [JsonPropertyName("filterEntries")]
    public List<string> FilterEntries { get; set; } = [];

    /// <summary>黑白双名单（#20）。</summary>
    [JsonPropertyName("whitelist")]
    public List<string> Whitelist { get; set; } = [];

    [JsonPropertyName("blacklist")]
    public List<string> Blacklist { get; set; } = [];

    /// <summary>主人放行范围（私聊 + 主人所在的群）。</summary>
    [JsonPropertyName("alwaysAllow")]
    public List<string> AlwaysAllow { get; set; } = [];

    [JsonPropertyName("groupCandidates")]
    public List<GroupCandidateDto> GroupCandidates { get; set; } = [];

    [JsonPropertyName("filterCount")]
    public int FilterCount { get; set; }

    [JsonPropertyName("candidates")]
    public List<AccessCandidateDto> Candidates { get; set; } = [];
}

public sealed class AccessCandidateDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;

    /// <summary>下拉框显示用。</summary>
    [JsonIgnore]
    public string Display => $"{Nickname}（{UserId}）";
}

/// <summary>群候选（名单条目用）。</summary>
public sealed class GroupCandidateDto
{
    [JsonPropertyName("groupId")]
    public string GroupId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonIgnore]
    public string Display => $"群 · {Name}（{SessionId}）";
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
