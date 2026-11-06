namespace ZipperAIQQ.Server.Billing;

/// <summary>余额采样参数（<c>billing</c> 配置节）。</summary>
public sealed record BillingOptions
{
    /// <summary>是否开启余额采样（只有 DeepSeek 端点支持）。</summary>
    public bool BalanceEnabled { get; init; } = true;

    /// <summary>采样间隔（分钟）。供应商没有历史接口，10 分钟够看趋势，也不会触发限频。</summary>
    public int SampleMinutes { get; init; } = 10;
}
