using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Failures;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Channel.Reliability;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 出站装饰器：真实发送结果要喂给健康状态机，熔断后快速失败。
/// </summary>
public sealed class HealthTrackingMessageChannelTests
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

    private static (HealthTrackingMessageChannel Channel, FakeChannel Inner, ChannelHealthMonitor Monitor) Create()
    {
        var registry = new ChannelHealthRegistry();
        var inner = new FakeChannel();
        var channel = new HealthTrackingMessageChannel(
            inner,
            registry,
            new LoggerConfiguration().CreateLogger());

        return (channel, inner, registry.Get("qq"));
    }

    [Fact]
    public async Task Success_keeps_the_circuit_closed()
    {
        var (channel, inner, monitor) = Create();

        await channel.SendAsync(Message, CancellationToken.None);

        Assert.Equal(1, inner.Calls);
        Assert.Equal(CircuitState.Closed, monitor.State);
        Assert.Equal(0, monitor.ConsecutiveFailures);
    }

    [Fact]
    public async Task Three_unavailable_failures_open_and_then_fail_fast()
    {
        var (channel, inner, monitor) = Create();
        inner.Failure = new ChannelUnavailableException("qq", "ws aborted");

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<ChannelUnavailableException>(
                () => channel.SendAsync(Message, CancellationToken.None));
        }

        Assert.True(monitor.IsChannelDown);
        Assert.Equal(3, inner.Calls);

        // 第 4 次：熔断中，不再真的去发
        await Assert.ThrowsAsync<ChannelUnavailableException>(
            () => channel.SendAsync(Message, CancellationToken.None));
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Send_failures_do_not_open_the_circuit()
    {
        var (channel, inner, monitor) = Create();
        inner.Failure = new ChannelSendFailedException("qq", "echo timeout");

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<ChannelSendFailedException>(
                () => channel.SendAsync(Message, CancellationToken.None));
        }

        Assert.False(monitor.IsChannelDown);
        Assert.Equal(0, monitor.ConsecutiveFailures);
    }

    [Fact]
    public void Platform_properties_are_delegated()
    {
        var (channel, _, _) = Create();

        Assert.Equal("qq", channel.Platform);
        Assert.Equal([ "qq" ], channel.Platforms);
        Assert.Equal(ChannelCapabilities.Text, channel.Capabilities);
    }
}
