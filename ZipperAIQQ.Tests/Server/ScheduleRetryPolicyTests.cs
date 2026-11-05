using ZipperAIQQ.Server.Scheduler;

namespace ZipperAIQQ.Tests.Server;

/// <summary>定时任务失败重试：指数退避 + 次数上限（F-4）。</summary>
public sealed class ScheduleRetryPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 10)]
    [InlineData(9, 10)]
    public void Delay_grows_then_caps_at_ten_minutes(int attempts, int expectedMinutes) =>
        Assert.Equal(expectedMinutes, ScheduleRetryPolicy.DelayFor(attempts).TotalMinutes);

    [Fact]
    public void Zero_attempts_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduleRetryPolicy.DelayFor(0));

    [Fact]
    public void Max_attempts_is_finite()
    {
        // 11-02 的教训：没有上限 = 每分钟重试四天
        Assert.Equal(10, ScheduleRetryPolicy.MaxAttempts);
    }
}
