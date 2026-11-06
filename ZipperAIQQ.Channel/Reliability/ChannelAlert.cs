namespace ZipperAIQQ.Channel.Reliability;

/// <summary>
/// 通道状态变化事件（供告警层消费）。
/// </summary>
/// <param name="Platform">平台。</param>
/// <param name="Opened"><c>true</c> = 刚熔断；<c>false</c> = 已恢复。</param>
/// <param name="Failures">熔断时的连续失败次数。</param>
/// <param name="Downtime">恢复时的断开时长。</param>
public sealed record ChannelAlert(string Platform, bool Opened, int Failures, TimeSpan? Downtime);
