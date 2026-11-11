using ZipperAIQQ.Server.Alerts;

namespace ZipperAIQQ.Tests.Server;

/// <summary>告警节流：熔断抖动时不刷屏。</summary>
public sealed class AlertThrottleTests
{
    [Fact]
    public void Same_state_within_window_is_suppressed()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new AlertThrottle(TimeSpan.FromMinutes(10), () => now);

        Assert.True(throttle.TryAcquire("douyin:down"));
        Assert.False(throttle.TryAcquire("douyin:down"));
    }

    [Fact]
    public void Different_states_and_platforms_are_independent()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new AlertThrottle(TimeSpan.FromMinutes(10), () => now);

        Assert.True(throttle.TryAcquire("douyin:down"));
        Assert.True(throttle.TryAcquire("douyin:up"));
        Assert.True(throttle.TryAcquire("qq:down"));
    }

    [Fact]
    public void After_window_the_alert_is_allowed_again()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new AlertThrottle(TimeSpan.FromMinutes(10), () => now);

        Assert.True(throttle.TryAcquire("douyin:down"));

        now = now.AddMinutes(11);

        Assert.True(throttle.TryAcquire("douyin:down"));
    }
}
