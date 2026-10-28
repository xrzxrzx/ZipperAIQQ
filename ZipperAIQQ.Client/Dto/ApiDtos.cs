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
