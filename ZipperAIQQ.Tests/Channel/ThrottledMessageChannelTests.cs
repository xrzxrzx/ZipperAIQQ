using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Failures;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Channel.Throttling;

namespace ZipperAIQQ.Tests.Channel;

/// <summary>
/// 节流层不能把失败吞掉、也不能被一次失败卡死（C-4）。
/// </summary>
public sealed class ThrottledMessageChannelTests
{
    private sealed class FakeChannel : IMessageChannel
    {
        public int Calls { get; private set; }

        public Exception? Failure { get; set; }

        public string Platform => "qq";

        public ChannelCapabilities Capabilities => ChannelCapabilities.Text;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(OutgoingMessage message, CancellationToken ct)
        {
            Calls++;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static readonly OutgoingMessage Message = new()
    {
        Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "10001" },
        Segments = [new TextSegment("hi")],
    };

    [Fact]
    public async Task Failure_is_propagated_not_swallowed()
    {
        var inner = new FakeChannel { Failure = new ChannelUnavailableException("qq", "down") };
        var channel = new ThrottledMessageChannel(inner, minIntervalMs: 0, maxConsecutive: 10, cooldownMs: 0);

        await Assert.ThrowsAsync<ChannelUnavailableException>(
            () => channel.SendAsync(Message, CancellationToken.None));
    }

    [Fact]
    public async Task Throttle_keeps_working_after_a_failure()
    {
        var inner = new FakeChannel { Failure = new ChannelSendFailedException("qq", "timeout") };
        var channel = new ThrottledMessageChannel(inner, minIntervalMs: 1, maxConsecutive: 10, cooldownMs: 0);

        await Assert.ThrowsAsync<ChannelSendFailedException>(
            () => channel.SendAsync(Message, CancellationToken.None));

        // 失败不该把闸门锁死：下一次仍然能正常发出
        inner.Failure = null;
        await channel.SendAsync(Message, CancellationToken.None);

        Assert.Equal(2, inner.Calls);
    }
}
