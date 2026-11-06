using System.Net.Sockets;
using System.Net.WebSockets;
using ZipperAIQQ.Channel.Failures;

namespace ZipperAIQQ.Tests.Channels;

/// <summary>
/// 失败分类：**语义异常优先，类型兜底**（问题记录 04 #7 的底座）。
/// </summary>
/// <remarks>
/// 这张表就是「要不要停手、要不要再叫模型」的依据 —— 分类错了，钱还会继续烧。
/// </remarks>
public sealed class SendFailureClassifierTests
{
    [Fact]
    public void Semantic_exceptions_win()
    {
        Assert.Equal(
            FailureKind.ChannelUnavailable,
            SendFailureClassifier.Classify(new ChannelUnavailableException("qq", "ws aborted")));

        Assert.Equal(
            FailureKind.ChannelSendFailed,
            SendFailureClassifier.Classify(new ChannelSendFailedException("qq", "echo timeout")));

        Assert.Equal(
            FailureKind.TargetInvalid,
            SendFailureClassifier.Classify(new TargetInvalidException("qq", "群不存在")));

        Assert.Equal(
            FailureKind.ContentInvalid,
            SendFailureClassifier.Classify(new ContentInvalidException("douyin", "内容为空")));
    }

    [Theory]
    [MemberData(nameof(LowerLevelCases))]
    public void Lower_level_exceptions_are_classified_by_type(Exception exception, FailureKind expected) =>
        Assert.Equal(expected, SendFailureClassifier.Classify(exception));

    public static TheoryData<Exception, FailureKind> LowerLevelCases() => new()
    {
        { new WebSocketException("socket is aborted"), FailureKind.ChannelUnavailable },
        { new ObjectDisposedException("socket"), FailureKind.ChannelUnavailable },
        { new SocketException((int)SocketError.ConnectionReset), FailureKind.ChannelUnavailable },
        { new IOException("connection reset"), FailureKind.ChannelUnavailable },
        { new TimeoutException("send timeout"), FailureKind.ChannelSendFailed },
    };

    [Fact]
    public void Cancellation_is_not_a_failure_kind()
    {
        // 取消是调用方的决定，不该被记成通道故障（否则会把正常关闭算进熔断）
        Assert.Equal(
            FailureKind.Unknown,
            SendFailureClassifier.Classify(new OperationCanceledException()));
    }

    [Fact]
    public void Unknown_exception_stays_unknown()
    {
        Assert.Equal(
            FailureKind.Unknown,
            SendFailureClassifier.Classify(new InvalidOperationException("目标不存在但没包语义异常")));
    }

    [Fact]
    public void Wrappers_are_unwrapped()
    {
        Assert.Equal(
            FailureKind.ChannelUnavailable,
            SendFailureClassifier.Classify(new AggregateException(new ChannelUnavailableException("qq", "down"))));

        Assert.Equal(
            FailureKind.ChannelSendFailed,
            SendFailureClassifier.Classify(
                new AggregateException(new TimeoutException("send timeout"))));
    }

    [Fact]
    public void Only_unavailable_counts_as_channel_down()
    {
        Assert.True(SendFailureClassifier.IsChannelDown(FailureKind.ChannelUnavailable));
        Assert.False(SendFailureClassifier.IsChannelDown(FailureKind.ChannelSendFailed));
        Assert.False(SendFailureClassifier.IsChannelDown(FailureKind.Unknown));
    }
}
