using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Tests.Channels;

/// <summary>
/// 抖音通道：帧解析、入站映射、ack 配对、分段、连接替换。
/// </summary>
/// <remarks>
/// 用假 <see cref="IDouyinConnection"/>（内存双向队列）而不是真 WebSocket：
/// 单测不连真实 sidecar（设计 §9 / 编码规范 §13）。
/// </remarks>
public sealed class DouyinChannelTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    // ─────────────────────────── 帧解析 / 入站映射 ───────────────────────────

    [Fact]
    public async Task ReceiveAsync_MalformedAndUnknownFrames_DoesNotBreakStream()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection();
        await channel.AttachAsync(connection);

        // 坏 JSON、缺 type、未知 type、inbound 缺会话字段 —— 全部只记日志，不能打断接收流
        connection.Push("{这不是 JSON");
        connection.Push("[]");
        connection.Push(Frame("nope", new { anything = 1 }));
        connection.Push(Frame(DouyinFrameTypes.Inbound, new { type = "message.private" }));
        connection.Push(PrivateInboundFrame(threadId: "10086", messageId: "m-ok", text: "还在吗"));

        var message = await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.Equal("m-ok", message.EventId);
        Assert.True(channel.IsConnected);
    }

    [Fact]
    public async Task ReceiveAsync_HelloFrame_SetsSelfIdAndRaisesEventOnce()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection();
        await channel.AttachAsync(connection);

        var raised = new List<string>();
        channel.SelfIdChanged += selfId => raised.Add(selfId);

        connection.Push(HelloFrame("self-1"));
        connection.Push(HelloFrame("self-1")); // 重复 hello 不应再触发
        connection.Push(PrivateInboundFrame(threadId: "10086", messageId: "m-barrier", text: "hi"));

        // 同一条连接上帧是顺序处理的：读到这条消息 ⇒ 上面两个 hello 都处理完了
        await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.Equal("self-1", channel.SelfId);
        Assert.Single(raised);
        Assert.Equal("self-1", raised[0]);
    }

    [Fact]
    public async Task ReceiveAsync_NativeGroupInbound_MapsChatSenderSegmentsAndTime()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection();
        await channel.AttachAsync(connection);

        const long platformTime = 1_730_000_000_000;
        connection.Push(Frame(DouyinFrameTypes.Inbound, new
        {
            type = "message.group",
            threadId = "123456",
            serverMessageId = "srv-1",
            time = platformTime,
            sender = new { userId = "u-9", nickname = "小明", displayName = "小明（群昵称）" },
            content = new { kind = "text", text = "大家好" },
            mentions = new object[] { new { userId = "7007", nickname = "小红" } },
            referenceInfo = new { serverMessageId = "srv-0" },
        }));

        var message = await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.Equal("douyin", message.Chat.Platform);
        Assert.Equal(ChatType.Group, message.Chat.Type);
        Assert.Equal("123456", message.Chat.Id);
        Assert.Equal("srv-1", message.EventId);

        // 平台时间戳原样映射，不取本地时钟
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(platformTime), message.OccurredAt);

        Assert.Equal("u-9", message.Sender.UserId);
        Assert.Equal("小明", message.Sender.Nickname);
        Assert.Equal("小明（群昵称）", message.Sender.GroupCard);
        Assert.False(message.Sender.IsFriend); // 群聊里「是不是好友」事件里没有

        // 引用在最前，正文与 @ 保持原顺序
        Assert.Equal(3, message.Segments.Count);
        Assert.Equal("srv-0", Assert.IsType<ReplySegment>(message.Segments[0]).MessageId);
        Assert.Equal("大家好", Assert.IsType<TextSegment>(message.Segments[1]).Text);
        Assert.Equal("7007", Assert.IsType<MentionSegment>(message.Segments[2]).UserId);
        Assert.Equal("srv-0", message.ReplyToMessageId);
    }

    [Fact]
    public async Task ReceiveAsync_NonNumericMention_DegradesToTextSegment()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection();
        await channel.AttachAsync(connection);

        // 群 @ 在 douyin-im 里要求数字 uid（设计 §10 风险 7）：非数字降级纯文本，别把整条消息弄坏
        connection.Push(Frame(DouyinFrameTypes.Inbound, new
        {
            type = "message.group",
            threadId = "123456",
            serverMessageId = "m-mention-bad",
            time = 1_730_000_000_000L,
            senderUid = "u-1",
            content = new { kind = "text", text = "看这个" },
            mentions = new object[] { new { userId = "sec_uid_abc", nickname = "小红" } },
        }));

        var message = await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.Equal(2, message.Segments.Count);
        Assert.Equal("看这个", Assert.IsType<TextSegment>(message.Segments[0]).Text);
        Assert.Equal("@小红", Assert.IsType<TextSegment>(message.Segments[1]).Text);
    }

    [Fact]
    public async Task SendAsync_NonNumericMention_DegradesToText()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection { Respond = AutoAck };
        await channel.AttachAsync(connection);

        var message = new OutgoingMessage
        {
            Chat = new ChatRef { Platform = DouyinChannel.PlatformName, Type = ChatType.Group, Id = "777" },
            Segments = [new TextSegment("看这个"), new MentionSegment("sec_uid_abc")],
        };

        await channel.SendAsync(message, CancellationToken.None);

        var segments = JsonNode.Parse(Assert.Single(connection.SentFrames))!["data"]!["segments"]!.AsArray();
        Assert.Equal(2, segments.Count);
        Assert.Equal("text", segments[1]!["type"]!.GetValue<string>());
        Assert.Equal("@sec_uid_abc", segments[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReceiveAsync_TranslatedShape_MapsSessionBackToChatId()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection();
        await channel.AttachAsync(connection);

        // 设计 §3.3 示例形状：只有 session + chatType + segments
        connection.Push(Frame(DouyinFrameTypes.Inbound, new
        {
            session = "douyin:g555",
            chatType = "group",
            messageId = "m-1",
            sender = new { userId = "u-1", nickname = "阿花" },
            segments = new object[]
            {
                new { type = "text", text = "在吗" },
                new { type = "mention", userId = "42" },
            },
        }));

        var message = await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.Equal(ChatType.Group, message.Chat.Type);
        Assert.Equal("555", message.Chat.Id); // session 的 g 前缀被剥掉
        Assert.Equal("m-1", message.EventId);
        Assert.Equal(2, message.Segments.Count);
        Assert.Equal("42", Assert.IsType<MentionSegment>(message.Segments[1]).UserId);
    }

    [Fact]
    public async Task ReceiveAsync_VideoAndFileContent_DegradeToPlaceholderText()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection();
        await channel.AttachAsync(connection);

        connection.Push(Frame(DouyinFrameTypes.Inbound, new
        {
            type = "message.private",
            threadId = "10086",
            serverMessageId = "m-video",
            time = 1_730_000_000_000,
            senderUid = "u-1",
            content = new { kind = "video" },
        }));

        var message = await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.Equal("[视频]", Assert.IsType<TextSegment>(Assert.Single(message.Segments)).Text);
        Assert.True(message.Sender.IsFriend); // 私聊 ⇒ 好友
    }

    [Fact]
    public async Task ReceiveAsync_StrangerMessage_IsDropped()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection();
        await channel.AttachAsync(connection);

        connection.Push(Frame(DouyinFrameTypes.Inbound, new
        {
            type = "message.stranger",
            threadId = "999",
            serverMessageId = "m-stranger",
            content = new { kind = "text", text = "陌生人" },
        }));
        connection.Push(PrivateInboundFrame(threadId: "10086", messageId: "m-friend", text: "好友"));

        // 若陌生人不丢，这里会先读到 m-stranger
        var message = await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.Equal("m-friend", message.EventId);
    }

    // ─────────────────────────── 发送 / ack 配对 ───────────────────────────

    [Fact]
    public async Task SendAsync_AckReceived_CompletesWithoutThrow()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection { Respond = AutoAck };
        await channel.AttachAsync(connection);

        await channel.SendAsync(Outgoing("你好"), CancellationToken.None);

        var sent = Assert.Single(connection.SentFrames);
        var frame = JsonNode.Parse(sent)!;
        Assert.Equal("outbound", frame["type"]!.GetValue<string>());
        Assert.Equal("douyin:p10086", frame["data"]!["session"]!.GetValue<string>());
        Assert.Equal("你好", frame["data"]!["segments"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task SendAsync_NoAck_ThrowsTimeout()
    {
        await using var channel = CreateChannel(sendTimeoutMs: 50);
        var connection = new FakeDouyinConnection(); // 不回 ack
        await channel.AttachAsync(connection);

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => channel.SendAsync(Outgoing("没人应答"), CancellationToken.None));

        Assert.Contains("ack", exception.Message);
    }

    [Fact]
    public async Task SendAsync_AckReportsFailure_ThrowsWithReason()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection
        {
            Respond = json =>
            {
                var requestId = RequestIdOf(json);
                return [Frame(DouyinFrameTypes.Ack, new { requestId, ok = false, error = "conversation-risk" })];
            },
        };
        await channel.AttachAsync(connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => channel.SendAsync(Outgoing("被风控拦住"), CancellationToken.None));

        Assert.Contains("conversation-risk", exception.Message);
    }

    [Fact]
    public async Task SendAsync_WithoutConnection_Throws()
    {
        await using var channel = CreateChannel();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => channel.SendAsync(Outgoing("没连上还发"), CancellationToken.None));

        Assert.Contains("未连接", exception.Message);
    }

    [Fact]
    public async Task SendAsync_LongText_SplitsIntoOneFramePerChunk()
    {
        await using var channel = CreateChannel(maxTextLength: 10);
        var connection = new FakeDouyinConnection { Respond = AutoAck };
        await channel.AttachAsync(connection);

        // 20 个字符 + 上限 10 → 两帧，各自 ≤ 10 字符
        await channel.SendAsync(Outgoing("0123456789abcdefghij"), CancellationToken.None);

        var frames = connection.SentFrames;
        Assert.Equal(2, frames.Count);

        foreach (var json in frames)
        {
            var text = JsonNode.Parse(json)!["data"]!["segments"]![0]!["text"]!.GetValue<string>();
            Assert.True(text.Length <= 10, $"分段长度 {text.Length} 超过上限");
        }

        var joined = string.Concat(frames.Select(json =>
            JsonNode.Parse(json)!["data"]!["segments"]![0]!["text"]!.GetValue<string>()));
        Assert.Equal("0123456789abcdefghij", joined);
    }

    [Fact]
    public async Task SendAsync_SegmentArray_SkipsReplyButKeepsMentionAndImage()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection { Respond = AutoAck };
        await channel.AttachAsync(connection);

        var message = new OutgoingMessage
        {
            Chat = new ChatRef { Platform = DouyinChannel.PlatformName, Type = ChatType.Group, Id = "777" },
            Segments =
            [
                new ReplySegment("srv-0"),
                new TextSegment("看图"),
                new MentionSegment("12345"),
                new ImageSegment("https://example.com/a.png", null),
            ],
            ReplyToMessageId = "srv-0",
        };

        await channel.SendAsync(message, CancellationToken.None);

        var data = JsonNode.Parse(Assert.Single(connection.SentFrames))!["data"]!;
        Assert.Equal("srv-0", data["replyTo"]!.GetValue<string>());

        var segments = data["segments"]!.AsArray();
        Assert.Equal(3, segments.Count); // 引用不进 segments
        Assert.Equal("text", segments[0]!["type"]!.GetValue<string>());
        Assert.Equal("mention", segments[1]!["type"]!.GetValue<string>());
        Assert.Equal("image", segments[2]!["type"]!.GetValue<string>());
    }

    // ─────────────────────────── 连接替换 ───────────────────────────

    [Fact]
    public async Task AttachAsync_SecondConnection_ClosesOldAndKeepsReceiving()
    {
        await using var channel = CreateChannel();
        var first = new FakeDouyinConnection();
        await channel.AttachAsync(first);

        first.Push(PrivateInboundFrame(threadId: "10086", messageId: "m-1", text: "第一条"));
        Assert.Equal("m-1", (await ReadMessageAsync(channel, TimeSpan.FromSeconds(5))).EventId);

        var second = new FakeDouyinConnection();
        await channel.AttachAsync(second);

        Assert.Equal(1, first.CloseCount); // 旧连接被关闭
        Assert.True(channel.IsConnected);

        // 接收流不中断：新连接的消息照常进来
        second.Push(PrivateInboundFrame(threadId: "10086", messageId: "m-2", text: "第二条"));
        Assert.Equal("m-2", (await ReadMessageAsync(channel, TimeSpan.FromSeconds(5))).EventId);
    }

    // ─────────────────────────── 候选查询 ───────────────────────────

    [Fact]
    public async Task GetFriendsAsync_ResultPaired_ReturnsContacts()
    {
        await using var channel = CreateChannel();
        var connection = new FakeDouyinConnection
        {
            Respond = json =>
            {
                var requestId = RequestIdOf(json);
                return
                [
                    Frame(DouyinFrameTypes.Result, new
                    {
                        requestId,
                        ok = true,
                        contacts = new object[]
                        {
                            new { userId = "u-1", nickname = "小明", remark = "同事" },
                            new { userId = "u-2", nickname = "阿花" },
                        },
                    }),
                ];
            },
        };
        await channel.AttachAsync(connection);

        var contacts = await channel.GetFriendsAsync(CancellationToken.None);

        Assert.Equal(2, contacts.Count);
        Assert.Equal("u-1", contacts[0].UserId);
        Assert.Equal("同事", contacts[0].Remark);
        Assert.Equal("阿花", contacts[1].Nickname);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DouyinChannel CreateChannel(
        int sendTimeoutMs = 2_000,
        int queryTimeoutMs = 1_000,
        int maxTextLength = 16_000) =>
        new(
            new DouyinOptions
            {
                Enabled = true,
                SendTimeoutMs = sendTimeoutMs,
                QueryTimeoutMs = queryTimeoutMs,
                MaxTextLength = maxTextLength,
            },
            Logger);

    /// <summary>拼一帧：<c>{type, time, data}</c>。</summary>
    private static string Frame(string type, object data) =>
        JsonSerializer.Serialize(new { type, time = 1_730_000_000_000L, data });

    private static string HelloFrame(string selfId) =>
        Frame(DouyinFrameTypes.Hello, new
        {
            version = "1",
            platform = "douyin",
            selfId,
            capabilities = new[] { "text", "image", "mention", "reply" },
        });

    private static string PrivateInboundFrame(string threadId, string messageId, string text) =>
        Frame(DouyinFrameTypes.Inbound, new
        {
            type = "message.private",
            threadId,
            serverMessageId = messageId,
            time = 1_730_000_000_000L,
            senderUid = "u-1",
            senderNickname = "小明",
            content = new { kind = "text", text },
        });

    /// <summary>任何 outbound / query 帧都回一个成功 ack。</summary>
    private static IEnumerable<string> AutoAck(string json)
    {
        var frame = JsonNode.Parse(json)!;
        if (frame["type"]!.GetValue<string>() != DouyinFrameTypes.Outbound)
        {
            return [];
        }

        return [Frame(DouyinFrameTypes.Ack, new { requestId = RequestIdOf(json), ok = true, error = (string?)null })];
    }

    private static string RequestIdOf(string json) =>
        JsonNode.Parse(json)!["data"]!["requestId"]!.GetValue<string>();

    private static OutgoingMessage Outgoing(string text) => new()
    {
        Chat = new ChatRef { Platform = DouyinChannel.PlatformName, Type = ChatType.Private, Id = "10086" },
        Segments = [new TextSegment(text)],
    };

    /// <summary>从通道路的事件流里读下一条消息（超时即失败）。</summary>
    private static async Task<IncomingMessage> ReadMessageAsync(DouyinChannel channel, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var inboundEvent in channel.ReceiveAsync(cts.Token))
            {
                if (inboundEvent is IncomingMessage message)
                {
                    return message;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"等待抖音入站消息超时（{timeout.TotalSeconds:0.#}s）。");
        }

        throw new InvalidOperationException("抖音事件流已结束，等不到消息。");
    }

    /// <summary>假的 sidecar 连接：内存双向队列 + 可编程应答。</summary>
    private sealed class FakeDouyinConnection : IDouyinConnection
    {
        private readonly System.Threading.Channels.Channel<string> _incoming =
            System.Threading.Channels.Channel.CreateUnbounded<string>();

        private readonly object _sync = new();
        private readonly List<string> _sent = [];
        private int _closed;

        /// <summary>收到一帧时返回要回推的帧（模拟 sidecar 的 ack / result）。</summary>
        public Func<string, IEnumerable<string>>? Respond { get; init; }

        public int CloseCount { get; private set; }

        public IReadOnlyList<string> SentFrames
        {
            get
            {
                lock (_sync)
                {
                    return _sent.ToArray();
                }
            }
        }

        public Task SendAsync(string json, CancellationToken ct)
        {
            if (Volatile.Read(ref _closed) == 1)
            {
                throw new InvalidOperationException("连接已关闭");
            }

            lock (_sync)
            {
                _sent.Add(json);
            }

            if (Respond is { } respond)
            {
                foreach (var reply in respond(json))
                {
                    Push(reply);
                }
            }

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var frame in _incoming.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return frame;
            }
        }

        public Task CloseAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                CloseCount++;
            }

            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }

        /// <summary>把一帧塞进"sidecar → Server"方向。</summary>
        public void Push(string json) => _incoming.Writer.TryWrite(json);
    }
}
