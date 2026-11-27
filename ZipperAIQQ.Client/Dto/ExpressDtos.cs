namespace ZipperAIQQ.Client.Dto;

/// <summary>
/// 表达档位状态（<c>getExpressStatus</c> / <c>setExpressLevel</c> 的共同形状）。
/// </summary>
/// <remarks>
/// 客户端**只回显服务端给的档位名与标签**，不自己拼：
/// 界面上的「亲近」必须是服务端真正在用的那一档，否则会出现「界面说亲近、说话还是日常」。
/// </remarks>
public sealed class ExpressStatusDto
{
    [System.Text.Json.Serialization.JsonPropertyName("level")]
    public string Level { get; set; } = "daily";

    [System.Text.Json.Serialization.JsonPropertyName("levelLabel")]
    public string LevelLabel { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("groupCap")]
    public string GroupCap { get; set; } = "explicit";

    [System.Text.Json.Serialization.JsonPropertyName("groupCapLabel")]
    public string GroupCapLabel { get; set; } = string.Empty;

    /// <summary><c>global</c> / <c>session</c>（只有在请求里带了 sessionId 时才可能是 session）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("source")]
    public string Source { get; set; } = "global";

    [System.Text.Json.Serialization.JsonPropertyName("sessionOverrides")]
    public List<ExpressSessionOverrideDto> SessionOverrides { get; set; } = [];

    [System.Text.Json.Serialization.JsonPropertyName("hygiene")]
    public ExpressHygieneDto Hygiene { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("lastChangedAt")]
    public long LastChangedAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("lastChangedBy")]
    public string? LastChangedBy { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("promptExpress")]
    public ExpressPromptDto PromptExpress { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("snapshots")]
    public List<ExpressSnapshotDto> Snapshots { get; set; } = [];

    // ── setExpressLevel 的额外字段 ──

    /// <summary>改了 n 项（**整数**，不是 bool —— `saveDouyin` 那次事故就是这么来的）。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("saved")]
    public int Saved { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("skipped")]
    public bool Skipped { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("snapshotId")]
    public long? SnapshotId { get; set; }
}

/// <summary>一条会话级档位覆盖。</summary>
public sealed class ExpressSessionOverrideDto
{
    [System.Text.Json.Serialization.JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("level")]
    public string Level { get; set; } = "daily";

    [System.Text.Json.Serialization.JsonPropertyName("levelLabel")]
    public string LevelLabel { get; set; } = string.Empty;

    public string Display => $"{SessionId}：{LevelLabel}";
}

/// <summary>免拒绝校准的状态（进程内计数，重启归零）。</summary>
public sealed class ExpressHygieneDto
{
    [System.Text.Json.Serialization.JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("regenerate")]
    public bool Regenerate { get; set; } = true;

    [System.Text.Json.Serialization.JsonPropertyName("hits")]
    public long Hits { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("regenerated")]
    public long Regenerated { get; set; }
}

/// <summary>规则段是否被自定义（正文不在这里，编辑走单独接口）。</summary>
public sealed class ExpressPromptDto
{
    [System.Text.Json.Serialization.JsonPropertyName("customized")]
    public bool Customized { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("length")]
    public int Length { get; set; }

    /// <summary>规则段全文（空 = 用内置默认）。编辑框要的就是它。</summary>
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>一个可回滚点。</summary>
public sealed class ExpressSnapshotDto
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public long Id { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("atMs")]
    public long AtMs { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("actor")]
    public string Actor { get; set; } = string.Empty;

    public string Display
    {
        get
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(AtMs).ToLocalTime();
            return $"#{Id} · {at:yyyy-MM-dd HH:mm:ss} · {Actor}";
        }
    }
}
