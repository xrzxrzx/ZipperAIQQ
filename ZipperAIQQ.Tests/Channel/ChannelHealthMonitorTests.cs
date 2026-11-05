using ZipperAIQQ.Channel.Failures;
using ZipperAIQQ.Channel.Reliability;

namespace ZipperAIQQ.Tests.Channel;

/// <summary>
/// 健康状态机：连续失败 → 熔断 → 冷却后半开（C 批共用，不按平台各写一遍）。
/// </summary>
public sealed class ChannelHealthMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8));

    private static ChannelHealthMonitor Monitor(int threshold = 3, int cooldownSeconds = 30) =>
        new("qq", threshold, TimeSpan.FromSeconds(cooldownSeconds));

    [Fact]
    public void Opens_after_threshold_unavailable_failures()
    {
        var monitor = Monitor();

        Assert.False(monitor.RecordFailure(FailureKind.ChannelUnavailable, Now));
        Assert.False(monitor.RecordFailure(FailureKind.ChannelUnavailable, Now));
        Assert.True(monitor.RecordFailure(FailureKind.ChannelUnavailable, Now));

        Assert.Equal(CircuitState.Open, monitor.State);
        Assert.True(monitor.IsChannelDown);
        Assert.Equal(Now, monitor.OpenedAt);
    }

    [Fact]
    public void Send_failures_do_not_open_the_circuit()
    {
        // 发送超时 / 目标非法是「单条消息」的问题，不是通道坏了
        var monitor = Monitor();

        for (var i = 0; i < 10; i++)
        {
            Assert.False(monitor.RecordFailure(FailureKind.ChannelSendFailed, Now));
            Assert.False(monitor.RecordFailure(FailureKind.TargetInvalid, Now));
        }

        Assert.Equal(CircuitState.Closed, monitor.State);
    }

    [Fact]
    public void Success_resets_the_counter()
    {
        var monitor = Monitor();
        monitor.RecordFailure(FailureKind.ChannelUnavailable, Now);
        monitor.RecordFailure(FailureKind.ChannelUnavailable, Now);

        Assert.True(monitor.RecordSuccess());
        Assert.Equal(0, monitor.ConsecutiveFailures);

        // 重置后再失败两次也不该熔断
        monitor.RecordFailure(FailureKind.ChannelUnavailable, Now);
        Assert.False(monitor.RecordFailure(FailureKind.ChannelUnavailable, Now));
        Assert.Equal(CircuitState.Closed, monitor.State);
    }

    [Fact]
    public void Half_open_only_after_cooldown()
    {
        var monitor = Monitor(cooldownSeconds: 30);
        monitor.RecordFailure(FailureKind.ChannelUnavailable, Now);
        monitor.RecordFailure(FailureKind.ChannelUnavailable, Now);
        monitor.RecordFailure(FailureKind.ChannelUnavailable, Now);

        Assert.False(monitor.TryHalfOpen(Now.AddSeconds(29)));
        Assert.True(monitor.TryHalfOpen(Now.AddSeconds(30)));
        Assert.Equal(CircuitState.HalfOpen, monitor.State);
    }

    [Fact]
    public void Failed_probe_reopens_immediately()
    {
        var monitor = Monitor(cooldownSeconds: 30);
        for (var i = 0; i < 3; i++)
        {
            monitor.RecordFailure(FailureKind.ChannelUnavailable, Now);
        }

        Assert.True(monitor.TryHalfOpen(Now.AddSeconds(30)));
        Assert.True(monitor.RecordFailure(FailureKind.ChannelUnavailable, Now.AddSeconds(31)));
        Assert.Equal(CircuitState.Open, monitor.State);
    }

    [Fact]
    public void Success_in_half_open_closes()
    {
        var monitor = Monitor(cooldownSeconds: 30);
        for (var i = 0; i < 3; i++)
        {
            monitor.RecordFailure(FailureKind.ChannelUnavailable, Now);
        }

        monitor.TryHalfOpen(Now.AddSeconds(30));
        Assert.True(monitor.RecordSuccess());
        Assert.Equal(CircuitState.Closed, monitor.State);
    }
}
