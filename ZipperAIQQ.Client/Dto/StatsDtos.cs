using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

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
