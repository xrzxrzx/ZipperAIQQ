namespace ZipperAIQQ.Server.Configuration;

/// <summary>
/// 通道可靠性参数（<c>channel</c> 配置节）。
/// </summary>
/// <remarks>
/// 全部用**秒 / 次数**而不是 <see cref="TimeSpan"/>：YAML 里写 <c>30</c> 比写 <c>00:00:30</c>
/// 更不容易写错，也和现有 <c>mail</c> / <c>douyin</c> 节的风格一致。
/// <para>
/// 这些值是**线上可调**的：熔断太敏感会让一条消息的问题把整条通道停掉，
/// 太迟钝又挡不住 11-05 那种循环。默认 3 次 / 30 秒是保守起点。
/// </para>
/// </remarks>
public sealed record ChannelReliabilityOptions
{
    /// <summary>同一通道同一状态的告警最短间隔（分钟）。熔断抖动时不再每 30 秒刷屏。</summary>
    public int AlertSuppressMinutes { get; init; } = 10;

    /// <summary>健康时的巡检间隔（秒）。</summary>
    public int PollSeconds { get; init; } = 5;

    /// <summary>首次重连退避（秒）。</summary>
    public int BackoffBaseSeconds { get; init; } = 1;

    /// <summary>退避上限（秒）—— 别把恢复拖到几分钟。</summary>
    public int BackoffMaxSeconds { get; init; } = 30;

    /// <summary>连续多少次「通道不可用」后熔断。</summary>
    public int OpenAfterFailures { get; init; } = 3;

    /// <summary>熔断冷却多久后放一次半开探测（秒）。</summary>
    public int OpenCooldownSeconds { get; init; } = 30;
}
