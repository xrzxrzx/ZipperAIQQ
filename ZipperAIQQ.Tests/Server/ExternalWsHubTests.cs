using System.Text.Json;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.External;

namespace ZipperAIQQ.Tests.Server;

/// <summary>外部 WS 订阅过滤（问题记录 03 #5）。</summary>
public sealed class ExternalWsHubTests
{
    private sealed class FakeChannel : IMessageChannel
    {
        public string Platform => "qq";

        public string? SelfId => "10001";

        public ChannelCapabilities Capabilities => ChannelCapabilities.Text;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public IAsyncEnumerable<InboundEvent> ReceiveAsync(CancellationToken ct) =>
            AsyncEnumerable.Empty<InboundEvent>();

        public Task SendAsync(OutgoingMessage message, CancellationToken ct) => Task.CompletedTask;
    }

    private static ExternalWsHub CreateHub() =>
        new(
            new LoggerConfiguration().CreateLogger(),
            new FakeChannel(),
            new ZipperAIQQ.Server.Configuration.ServerOptions
            {
                NapCatWebSocketUrl = "ws://test",
                LlmApiKey = "test",
                LlmBaseUrl = "https://test",
                LlmModel = "test",
                SelfId = "10001",
            });

    private static IncomingMessage Message() => new()
    {
        Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "1" },
        OccurredAt = DateTimeOffset.Now,
        Sender = new SenderInfo { UserId = "1", Nickname = "A" },
        Segments = [new TextSegment("hi")],
    };

    private static IncomingNotice Notice() => new()
    {
        Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "1" },
        OccurredAt = DateTimeOffset.Now,
        Type = NoticeType.Poke,
        RawType = "notice.poke",
        Actor = new SenderInfo { UserId = "1", Nickname = "A" },
        Target = new SenderInfo { UserId = "10001", Nickname = "me" },
    };

    [Fact]
    public void MessageSubscription_OnlyReceivesMessages()
    {
        var hub = CreateHub();
        var session = hub.Add("test");
        session.SetSubscriptions(["message"]);

        hub.PublishMessage(Message());
        Assert.True(session.Reader.TryRead(out var frame));

        using var doc = JsonDocument.Parse(frame);
        Assert.Equal("message", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        hub.PublishNotice(Notice());
        Assert.False(session.Reader.TryRead(out _));
    }

    [Fact]
    public void NoticeSubscription_OnlyReceivesNotices()
    {
        var hub = CreateHub();
        var session = hub.Add("test");
        session.SetSubscriptions(["notice"]);

        hub.PublishNotice(Notice());
        Assert.True(session.Reader.TryRead(out var frame));

        using var doc = JsonDocument.Parse(frame);
        Assert.Equal("notice", doc.RootElement.GetProperty("data").GetProperty("kind").GetString());

        hub.PublishMessage(Message());
        Assert.False(session.Reader.TryRead(out _));
    }

    [Fact]
    public void Hello_IncludesSelfId()
    {
        var hub = CreateHub();
        var session = hub.Add("test");

        hub.PublishHello(session);

        Assert.True(session.Reader.TryRead(out var frame));
        using var doc = JsonDocument.Parse(frame);
        Assert.Equal("10001", doc.RootElement.GetProperty("data").GetProperty("selfId").GetString());
    }
}
