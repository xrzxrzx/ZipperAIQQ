using ZipperAIQQ.Agent.Cost;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>通道熔断时的短路判据（F-3）。</summary>
public sealed class TurnShortCircuitTests
{
    [Theory]
    [InlineData(CallPurpose.MailDigest)]
    [InlineData(CallPurpose.Compression)]
    [InlineData(CallPurpose.DriftRetry)]
    [InlineData(CallPurpose.SchedulePolish)]
    [InlineData(CallPurpose.Judge)]
    public void Non_critical_turns_skip_when_channel_is_down(CallPurpose purpose) =>
        Assert.True(TurnShortCircuit.ShouldSkip(channelDown: true, purpose));

    [Theory]
    [InlineData(CallPurpose.UserReply)]
    [InlineData(CallPurpose.Command)]
    [InlineData(CallPurpose.Unknown)]
    public void Critical_turns_still_run(CallPurpose purpose) =>
        Assert.False(TurnShortCircuit.ShouldSkip(channelDown: true, purpose));

    [Fact]
    public void Healthy_channel_never_skips() =>
        Assert.False(TurnShortCircuit.ShouldSkip(channelDown: false, CallPurpose.MailDigest));
}
