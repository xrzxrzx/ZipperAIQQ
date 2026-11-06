namespace ZipperAIQQ.Server.Billing;

/// <summary>额度闸门参数（<c>quota</c> 配置节；D-13：默认关、日窗口、70% 预警）。</summary>
public sealed record QuotaOptions
{
    /// <summary>开关；关闭时行为与过去完全一致。</summary>
    public bool Enabled { get; init; }

    /// <summary>窗口：<c>hour</c> / <c>day</c> / <c>month</c>。</summary>
    public string Window { get; init; } = "day";

    /// <summary>硬线金额（元）。</summary>
    public decimal LimitCny { get; init; } = 5m;

    /// <summary>预警比例（0~1）。</summary>
    public decimal WarnRatio { get; init; } = 0.7m;
}
