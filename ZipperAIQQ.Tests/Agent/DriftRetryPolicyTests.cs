using ZipperAIQQ.Agent.Reply;
using ZipperAIQQ.Channel.Failures;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// 漂移重试判据：**通道坏了就不再问模型**（问题记录 04 #7）。
/// </summary>
public sealed class DriftRetryPolicyTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(FailureKind.Unknown, true)]
    [InlineData(FailureKind.ChannelSendFailed, true)]
    [InlineData(FailureKind.ChannelUnavailable, false)]
    [InlineData(FailureKind.TargetInvalid, false)]
    [InlineData(FailureKind.ContentInvalid, false)]
    [InlineData(FailureKind.RateLimited, false)]
    [InlineData(FailureKind.ModelFatal, false)]
    public void Retry_only_when_retrying_can_help(FailureKind? lastFailure, bool expected) =>
        Assert.Equal(expected, DriftRetryPolicy.ShouldRetry(0, "说到一半", lastFailure));

    [Fact]
    public void Nothing_sent_and_no_text_is_not_a_drift()
    {
        Assert.False(DriftRetryPolicy.ShouldRetry(0, null, null));
        Assert.False(DriftRetryPolicy.ShouldRetry(0, "   ", null));
    }

    [Fact]
    public void Already_sent_something_never_retries()
    {
        // 有副作用就不能重跑，否则会重复发消息
        Assert.False(DriftRetryPolicy.ShouldRetry(1, "已经发出去了", null));
    }
}
