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
        DateTimeOffset.FromUnixTimeMilliseconds(OccurredAt).LocalDateTime.ToString("MM-dd HH:mm:ss");

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

    /// <summary>
    /// 中文名（#6）：优先用服务端 <c>kindLabel</c>；**旧服务端没给时用客户端内置表兜底**，
    /// 免得界面上出现「上下两排英文」。
    /// </summary>
    [JsonIgnore]
    public string KindTitle =>
        !string.IsNullOrWhiteSpace(KindLabel)
            ? KindLabel
            : KnownLabels.TryGetValue(Kind, out var label) ? label : Kind;

    private static readonly Dictionary<string, string> KnownLabels = new(StringComparer.Ordinal)
    {
        ["llm.call"] = "LLM 调用",
        ["llm.retry"] = "LLM 重试（漂移纠正）",
        ["retrieval"] = "记忆检索",
        ["retrieval.hit"] = "检索命中",
        ["jev.judge"] = "JEV 判断",
        ["jev.degrade"] = "JEV 降级",
        ["summary"] = "上下文压缩",
        ["tool.send_message"] = "发送消息",
        ["embedding.backfill"] = "向量回填",
        ["tool.web_search"] = "网页搜索",
        ["tool.fetch_url"] = "网页抓取",
    };

    [JsonIgnore]
    public string CountText => Count.ToString("N0");

    [JsonIgnore]
    public string AvgMsText => $"{AvgMs:F0} ms";

    [JsonIgnore]
    public string TokenText => InputTokens == 0 && OutputTokens == 0
        ? "—"
        : $"{InputTokens:N0} / {OutputTokens:N0}";
}

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

/// <summary>按平台读取（<c>getAccess</c> / <c>getOwner</c>）：S3-C 起带平台，省略 = qq。</summary>
public sealed class PlatformQueryDto
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "qq";
}

/// <summary>设置主人（<c>setOwner</c>）：S3-C 起按平台分别设置，互不覆盖。</summary>
public sealed class SetOwnerRequestDto
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "qq";

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;
}

/// <summary>人员与名单（<c>getAccess</c>）。</summary>
public sealed class AccessDto
{
    /// <summary>
    /// 本次响应对应的平台（S3-C 回显）。**旧服务端不返回 → 空串**，
    /// 客户端按"请求时带的平台"处理，别拿默认值冒充回显。
    /// </summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    /// <summary>
    /// 可切换的平台列表（如 <c>["qq"]</c>）。**旧服务端不返回 → 空列表**，
    /// 客户端退化成只有一个默认平台，不报错。
    /// </summary>
    [JsonPropertyName("platforms")]
    public List<string> Platforms { get; set; } = [];

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

/// <summary>反向 WS 监听块（问题记录 03 #5）。</summary>
public sealed class ReverseServerDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

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

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

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

/// <summary>系统提示词（与人设分开）。</summary>
public sealed class SystemPromptDto{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("defaultText")]
    public string DefaultText { get; set; } = string.Empty;
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

/// <summary>第三方平台配置总览（<c>getPlatforms</c>，S3-F）。</summary>
/// <remarks>
/// 旧服务端没有这个 action → 调用会抛 <see cref="Services.ApiCallException"/>；
/// 页面必须**降级**（邮件页仍走 <c>listMailAccounts</c>，抖音页提示"需要新服务端"），不能整页白屏。
/// </remarks>
public sealed class PlatformsDto
{
    /// <summary>平台清单（QQ / 抖音…）：页面顶部状态点用。</summary>
    [JsonPropertyName("platforms")]
    public List<PlatformInfoDto> Platforms { get; set; } = [];

    /// <summary>邮件块；**缺失 → null**（不是空对象），页面按"服务端没给"展示。</summary>
    [JsonPropertyName("mail")]
    public MailPlatformDto? Mail { get; set; }

    /// <summary>抖音块；**缺失 → null**。</summary>
    [JsonPropertyName("douyin")]
    public DouyinPlatformDto? Douyin { get; set; }
}

/// <summary>一个平台的连通概况（<c>getPlatforms.platforms[]</c>）。</summary>
public sealed class PlatformInfoDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    /// <summary>平台侧自己的账号 ID（如 QQ 号）；未连上时可能为空。</summary>
    [JsonPropertyName("selfId")]
    public string? SelfId { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>该平台支持的能力名；旧服务端没给 → 空表。</summary>
    [JsonPropertyName("features")]
    public List<string> Features { get; set; } = [];
}

/// <summary>邮件平台块（<c>getPlatforms.mail</c>）。</summary>
public sealed class MailPlatformDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>汇总是否再交人设 agent 润色（true = 用人设语气转告，false = 直接发汇总文本）。</summary>
    [JsonPropertyName("personaDigest")]
    public bool PersonaDigest { get; set; }

    [JsonPropertyName("accounts")]
    public List<MailAccountDto> Accounts { get; set; } = [];
}

/// <summary>
/// 一个收信账号（<c>listMailAccounts</c> 数组项 / <c>saveMailAccount</c> 回显）。
/// </summary>
/// <remarks>
/// ⚠️ **契约里没有明文、也没有密文**：只有 <see cref="HasSecret"/>。
/// 要换授权码只能整条重填（<c>secret</c> 缺省 = 保留旧密文）。
/// </remarks>
public sealed class MailAccountDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("imapHost")]
    public string ImapHost { get; set; } = string.Empty;

    [JsonPropertyName("imapPort")]
    public int ImapPort { get; set; } = 993;

    [JsonPropertyName("useSsl")]
    public bool UseSsl { get; set; } = true;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>最近一次同步（Unix 毫秒）；**没同步过是 null**，别当 0 显示成 1970。</summary>
    [JsonPropertyName("lastSyncAt")]
    public long? LastSyncAt { get; set; }

    /// <summary>上次同步失败的原因；正常时 null。</summary>
    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    /// <summary>是否已存授权码（**只有布尔，永不放凭据**）。</summary>
    [JsonPropertyName("hasSecret")]
    public bool HasSecret { get; set; }
}

/// <summary>抖音平台块（<c>getPlatforms.douyin</c>）。**不含 token 明文**，只给 <see cref="TokenConfigured"/>。</summary>
public sealed class DouyinPlatformDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("selfId")]
    public string? SelfId { get; set; }

    [JsonPropertyName("tokenConfigured")]
    public bool TokenConfigured { get; set; }

    /// <summary>出站 ack 超时（毫秒）；服务端默认 15000。</summary>
    [JsonPropertyName("sendTimeoutMs")]
    public int SendTimeoutMs { get; set; } = 15_000;

    /// <summary>文本分段阈值（字符）；服务端默认 16000。</summary>
    [JsonPropertyName("maxTextLength")]
    public int MaxTextLength { get; set; } = 16_000;

    /// <summary>联系人 / 群查询超时（毫秒）；服务端默认 10000。</summary>
    [JsonPropertyName("queryTimeoutMs")]
    public int QueryTimeoutMs { get; set; } = 10_000;

    /// <summary>sidecar 连入的端点（<c>/ws/channel</c>）。</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "/ws/channel";

    /// <summary>
    /// 登录态：<c>online</c> / <c>waitingQr</c> / <c>loggedOut</c> / <c>error</c> / <c>unknown</c>。
    /// **旧服务端不返回 → 空串**，客户端据此把「登录 / 退出」按钮禁用（不假装支持）。
    /// </summary>
    [JsonPropertyName("loginState")]
    public string LoginState { get; set; } = string.Empty;
}

/// <summary>抖音登录态详情（<c>getDouyinLogin</c> / <c>startDouyinLogin</c>）。</summary>
public sealed class DouyinLoginDto
{
    /// <summary>online / waitingQr / loggedOut / error / unknown。</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "unknown";

    [JsonPropertyName("selfId")]
    public string? SelfId { get; set; }

    /// <summary>登录二维码（**PNG 的 base64，无 <c>data:</c> 前缀**）；没有时 null。</summary>
    [JsonPropertyName("qrcodeBase64")]
    public string? QrcodeBase64 { get; set; }

    /// <summary>二维码生成时间（Unix 毫秒，UTC）；没有二维码时为 0。</summary>
    [JsonPropertyName("qrcodeAt")]
    public long QrcodeAt { get; set; }

    /// <summary>出错时的原因（<c>state = error</c> 才有）；其它状态通常 null。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>退出抖音登录的结果（<c>logoutDouyin</c>）。</summary>
/// <remarks>只退出**登录态**，sidecar 进程继续跑（不会被本操作杀掉）。</remarks>
public sealed class DouyinLogoutResultDto
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "loggedOut";

    [JsonPropertyName("loggedOut")]
    public bool LoggedOut { get; set; }
}

/// <summary>保存抖音配置的结果（<c>saveDouyin</c>）。</summary>
public sealed class SaveDouyinResultDto
{
    /// <summary>真正改了几个字段（**数字**，不是 bool）。</summary>
    /// <remarks>
    /// ⚠️ 这里曾经写成 <c>bool</c>，而服务端返回的是计数（<c>"saved":2</c>）→
    /// JsonSerializer 直接抛异常，界面报「服务端返回的不是契约 JSON」，抖音配置怎么都存不上
    /// （用户实测 2026-10-28）。以后改这个契约**两边一起改**。
    /// </remarks>
    [JsonPropertyName("saved")]
    public int Saved { get; set; }

    /// <summary>没有任何字段变化（此时 <see cref="Saved"/> = 0）。</summary>
    [JsonPropertyName("skipped")]
    public bool Skipped { get; set; }

    /// <summary>真正变了的字段名；没变化 → 空表。</summary>
    [JsonPropertyName("changed")]
    public List<string> Changed { get; set; } = [];

    /// <summary>保存后 sidecar 当前是否连着（配置改完不一定立刻重连）。</summary>
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }
}

/// <summary>保存邮件配置的结果（<c>saveMail</c>：启用开关 + 人设润色开关，都是运行时热切）。</summary>
/// <remarks>
/// 值没变时服务端回 <c>saved = 0 / skipped = true</c>，**不算错**；
/// <see cref="Enabled"/> / <see cref="PersonaDigest"/> 是落库后的真值 —— 界面以它们为准，别拿自己拨的值当结果。
/// </remarks>
public sealed class SaveMailResultDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>人设润色的真值（与启用开关同一次响应回显）。</summary>
    [JsonPropertyName("personaDigest")]
    public bool PersonaDigest { get; set; }

    /// <summary>实际改动的键数：0 = 本来就是这值（配合 <see cref="Skipped"/>）。</summary>
    [JsonPropertyName("saved")]
    public int Saved { get; set; }

    [JsonPropertyName("skipped")]
    public bool Skipped { get; set; }
}
