using ZipperAIQQ.Channel.Reliability;

namespace ZipperAIQQ.Tests.Channel;

/// <summary>退避策略：纯计算，平台无关（C 批复用的核心）。</summary>
public sealed class ReconnectPolicyTests
{
    private static readonly ReconnectPolicy Policy = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(6, 30)]
    [InlineData(100, 30)]
    public void Delay_doubles_then_caps(int attempt, double expectedSeconds) =>
        Assert.Equal(expectedSeconds, Policy.DelayFor(attempt).TotalSeconds);

    [Fact]
    public void Negative_attempt_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy.DelayFor(-1));

    [Fact]
    public void Jitter_stays_within_bounds()
    {
        var random = new Random(1234);

        for (var i = 0; i < 50; i++)
        {
            var delay = Policy.DelayFor(5, random);

            // 30s ±20% → 24s~36s；封顶仍是 30s，抖动后不应超过 MaxDelay
            Assert.InRange(delay.TotalSeconds, 0.1, 30);
        }
    }

    [Fact]
    public void Jitter_is_reproducible_with_the_same_seed()
    {
        var a = Policy.DelayFor(2, new Random(42));
        var b = Policy.DelayFor(2, new Random(42));

        Assert.Equal(a, b);
    }
}
