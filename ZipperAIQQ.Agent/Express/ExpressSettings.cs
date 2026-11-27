namespace ZipperAIQQ.Agent.Express;

/// <summary>
/// 表达档位的**当轮生效快照**（Agent 侧只读）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么用快照而不是让 Agent 直接读配置库：一轮对话里档位被读到 3 次
/// （注入 / 预算 / 卫生），中途被客户端改档会让同一轮出现「规则段是亲近、预算按日常算」的半套口径。
/// 快照在轮次开始时取一次，整轮自洽。
/// </para>
/// <para>
/// 与 Server 侧 <c>ExpressOptionsService</c> 的对应关系：那个负责落库与热更新，
/// 这里只是「Agent 需要知道的那些字段」——Agent 不该知道键名，也不该知道快照表。
/// </para>
/// </remarks>
public sealed record ExpressSettings
{
    /// <summary>全局默认档。</summary>
    public ExpressLevel Level { get; init; } = ExpressLevel.Daily;

    /// <summary>
    /// 群聊上限（只收紧不放宽）。默认 <see cref="ExpressLevel.Explicit"/> = 不额外限制
    /// （用户 2026-11-20 定「都开放」）。
    /// </summary>
    public ExpressLevel GroupCap { get; init; } = ExpressLevel.Explicit;

    /// <summary>会话级覆盖（键 = 完整会话 ID，如 <c>douyin:p1000000000</c>）。</summary>
    public IReadOnlyDictionary<string, ExpressLevel> SessionOverrides { get; init; } =
        new Dictionary<string, ExpressLevel>(StringComparer.Ordinal);

    /// <summary>免拒绝校准总开关。</summary>
    public bool HygieneEnabled { get; init; } = true;

    /// <summary>命中后是否重生成一次（关掉只计数）。</summary>
    public bool HygieneRegenerate { get; init; } = true;

    /// <summary>
    /// 规则段覆盖文本（<c>prompt.express</c>）：非空即**整段替换**内置规则段。
    /// </summary>
    /// <remarks>与「系统提示词」同口径 —— 覆盖是替换，不是追加；留空 = 用内置默认。</remarks>
    public string? CustomRules { get; init; }

    /// <summary>上次改动时间（UTC ms）；0 = 从未改过。</summary>
    public long UpdatedAtMs { get; init; }

    /// <summary>上次改动的操作者（<c>http-client</c> / <c>startup</c> …）。</summary>
    public string? UpdatedBy { get; init; }
}
