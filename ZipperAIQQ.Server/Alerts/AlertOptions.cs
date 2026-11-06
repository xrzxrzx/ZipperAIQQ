namespace ZipperAIQQ.Server.Alerts;

/// <summary>告警策略参数。</summary>
public sealed record AlertOptions
{
    /// <summary>同一 (类型, 平台) 的抑制窗口（分钟）。</summary>
    /// <remarks>断网时每个失败都报一遍，会把仅剩的通道也打满（R-01 的抑制要求）。</remarks>
    public int SuppressMinutes { get; init; } = 10;
}
