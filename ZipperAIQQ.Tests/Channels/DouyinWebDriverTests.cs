using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Tests.Channels;

/// <summary>
/// 浏览器底座（B1）在**服务端**的行为：驱动闸门、登录挑战、页面体征、ack 的降级/失败码。
/// </summary>
/// <remarks>
/// 这几条都是"接错了也不会崩、但会静默错"的地方：
/// 驱动闸门不生效 → 旧 sidecar 把新 sidecar 顶掉（又回到 10509）；
/// 挑战不回传 → 用户只看到状态卡在等待扫码；
/// ack 的 degraded 丢掉 → 模型以为引用发出去了。
/// </remarks>
public sealed class DouyinWebDriverTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    // ─────────────────────────── 帧解析（纯函数） ───────────────────────────

    [Fact]
    public void ParseChallenge_sms_and_slider()
    {
        var sms = DouyinProtocol.ParseChallenge(new JsonObject
        {
            ["kind"] = "sms",
            ["hint"] = "输入手机收到的 6 位验证码",
        });

        Assert.NotNull(sms);
        Assert.Equal(DouyinChallengeKind.Sms, sms!.Kind);
        Assert.Null(sms.BgBase64);

        var slider = DouyinProtocol.ParseChallenge(new JsonObject
        {
            ["kind"] = "Slider", // 大小写不该影响
            ["bgBase64"] = "QUJD",
            ["pieceBase64"] = "REVG",
        });

        Assert.NotNull(slider);
        Assert.Equal(DouyinChallengeKind.Slider, slider!.Kind);
        Assert.Equal("QUJD", slider.BgBase64);
        Assert.Equal("REVG", slider.PieceBase64);
    }

    [Fact]
    public void ParseChallenge_rejects_unknown_and_missing_kind()
    {
        Assert.Null(DouyinProtocol.ParseChallenge(new JsonObject { ["hint"] = "?" }));
        Assert.Null(DouyinProtocol.ParseChallenge(new JsonObject { ["kind"] = "voice" }));
        Assert.Null(DouyinProtocol.ParseChallenge(null));
    }

    [Fact]
    public void BuildChallengeAnswer_carries_kind_and_value()
    {
        var json = DouyinProtocol.BuildChallengeAnswer("slider", "128");
        var frame = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(DouyinFrameTypes.LoginChallengeAnswer, frame["type"]!.GetValue<string>());
        Assert.Equal("slider", frame["data"]!["kind"]!.GetValue<string>());
        Assert.Equal("128", frame["data"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public void ParseBrowserState_reads_page_memory_and_note()
    {
        // 用线上形状（JSON 文本解析出来的 JsonObject）而不是手搭对象：
        // 数字在 JsonNode 里到底是 int / long / double，直接决定 GetLong 能不能取值
        var state = DouyinProtocol.ParseBrowserState(JsonNode.Parse(
            """{"page":"recycling","memoryMb":1234,"lastAction":"回收重建","note":"运行 6.2h 达到 6h"}""")!.AsObject());

        Assert.NotNull(state);
        Assert.Equal("recycling", state!.Page);
        Assert.Equal(1234, state.MemoryMb);
        Assert.Equal("回收重建", state.LastAction);
        Assert.Equal("运行 6.2h 达到 6h", state.Note);

        Assert.Null(DouyinProtocol.ParseBrowserState(new JsonObject { ["memoryMb"] = 1 }));
    }

    [Fact]
    public void ParseAck_reads_code_and_degraded()
    {
        var ack = DouyinProtocol.ParseAck(new JsonObject
        {
            ["requestId"] = "r1",
            ["ok"] = false,
            ["error"] = "发送后回读不到刚发出的内容",
            ["code"] = "readback",
        });

        Assert.NotNull(ack);
        Assert.False(ack!.Ok);
        Assert.Equal("readback", ack.Code);
        Assert.Null(ack.Degraded);

        var degraded = DouyinProtocol.ParseAck(new JsonObject
        {
            ["requestId"] = "r2",
            ["ok"] = true,
            ["degraded"] = "reply_to",
        });

        Assert.NotNull(degraded);
        Assert.Equal("reply_to", degraded!.Degraded);
        Assert.Null(degraded.Code);
    }

    [Fact]
    public void ParseHello_reads_driver_and_defaults_to_null_for_legacy()
    {
        var web = DouyinProtocol.ParseHello(new JsonObject
        {
            ["version"] = "1",
            ["platform"] = "douyin",
            ["driver"] = "web",
            ["capabilities"] = new JsonArray("text"),
        });

        Assert.Equal("web", web!.Driver);

        var legacy = DouyinProtocol.ParseHello(new JsonObject { ["version"] = "1" });
        Assert.Null(legacy!.Driver); // 老 sidecar 不带 driver → 由通道按 im 处理
    }

    // ─────────────────────────── 驱动闸门 ───────────────────────────

    [Fact]
    public async Task Hello_driver_mismatch_rejects_connection()
    {
        await using var channel = CreateChannel(DouyinDriver.Web);
        var connection = new FakeConnection();
        await channel.AttachAsync(connection);

        connection.Push(HelloFrame("im", selfId: "self-1"));
        connection.Push(PrivateInboundFrame("10086", "m-after-reject", "should not arrive"));

        // 等到被拒这件事发生：连接被关、通道不再连着
        await WaitUntilAsync(() => connection.CloseCount == 1, TimeSpan.FromSeconds(5));

        Assert.Contains("im", channel.DriverRejectNote, StringComparison.Ordinal);
        Assert.Contains("web", channel.DriverRejectNote, StringComparison.Ordinal);
        Assert.False(channel.IsConnected);
        Assert.Null(channel.Driver);
    }

    [Fact]
    public async Task Hello_driver_match_is_accepted_and_selfId_applied()
    {
        await using var channel = CreateChannel(DouyinDriver.Web);
        var connection = new FakeConnection();
        await channel.AttachAsync(connection);

        connection.Push(HelloFrame("web", selfId: "self-9"));
        connection.Push(PrivateInboundFrame("10086", "m-ok", "还在吗"));

        var message = await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.Equal("m-ok", message.EventId);
        Assert.Equal("web", channel.Driver);
        Assert.Equal("self-9", channel.SelfId);
        Assert.Null(channel.DriverRejectNote);
        Assert.Equal(0, connection.CloseCount);
    }

    [Fact]
    public async Task Legacy_sidecar_without_driver_counts_as_im()
    {
        await using var channel = CreateChannel(DouyinDriver.Im);
        var connection = new FakeConnection();
        await channel.AttachAsync(connection);

        // 老 sidecar 的 hello 没有 driver 字段
        connection.Push(Frame(DouyinFrameTypes.Hello, new
        {
            version = "1",
            platform = "douyin",
            selfId = "self-1",
            capabilities = new[] { "text" },
        }));
        connection.Push(PrivateInboundFrame("10086", "m-legacy", "hi"));

        var message = await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.Equal("m-legacy", message.EventId);
        Assert.Equal(DouyinDriver.Im, channel.Driver);
    }

    // ─────────────────────────── 登录挑战 ───────────────────────────

    [Fact]
    public async Task Challenge_frame_is_stored_and_answer_is_sent_back()
    {
        await using var channel = CreateChannel(DouyinDriver.Web);
        var connection = new FakeConnection();
        await channel.AttachAsync(connection);

        connection.Push(HelloFrame("web", "self-1"));
        connection.Push(Frame(DouyinFrameTypes.LoginChallenge, new
        {
            kind = "sms",
            hint = "输入手机收到的 6 位验证码",
        }));
        connection.Push(PrivateInboundFrame("10086", "m-barrier", "?"));

        // 用一条入站消息当屏障：读到它 ⇒ 前面的帧都处理完了
        await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.NotNull(channel.Challenge);
        Assert.Equal(DouyinChallengeKind.Sms, channel.Challenge!.Kind);

        await channel.SubmitChallengeAsync("sms", "123456", CancellationToken.None);

        var answer = connection.SentFrames
            .Select(json => JsonNode.Parse(json)!.AsObject())
            .Single(frame => frame["type"]!.GetValue<string>() == DouyinFrameTypes.LoginChallengeAnswer);

        Assert.Equal("sms", answer["data"]!["kind"]!.GetValue<string>());
        Assert.Equal("123456", answer["data"]!["value"]!.GetValue<string>());

        // 交出去之后本地就清掉（防连点两次发两遍）
        Assert.Null(channel.Challenge);
    }

    [Fact]
    public async Task SubmitChallenge_without_pending_challenge_throws()
    {
        await using var channel = CreateChannel(DouyinDriver.Web);
        var connection = new FakeConnection();
        await channel.AttachAsync(connection);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => channel.SubmitChallengeAsync("sms", "123456", CancellationToken.None));

        Assert.Contains("没有等待回填", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitChallenge_with_wrong_kind_throws()
    {
        await using var channel = CreateChannel(DouyinDriver.Web);
        var connection = new FakeConnection();
        await channel.AttachAsync(connection);

        connection.Push(Frame(DouyinFrameTypes.LoginChallenge, new { kind = "slider" }));
        connection.Push(PrivateInboundFrame("10086", "m-barrier", "?"));
        await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => channel.SubmitChallengeAsync("sms", "123456", CancellationToken.None));

        Assert.Contains("类型不匹配", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Online_state_clears_challenge()
    {
        await using var channel = CreateChannel(DouyinDriver.Web);
        var connection = new FakeConnection();
        await channel.AttachAsync(connection);

        connection.Push(HelloFrame("web", "self-1"));
        connection.Push(Frame(DouyinFrameTypes.LoginChallenge, new { kind = "sms" }));
        connection.Push(Frame(DouyinFrameTypes.LoginState, new { state = DouyinLoginState.Online, selfId = "self-1" }));
        connection.Push(PrivateInboundFrame("10086", "m-barrier", "?"));
        await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.Null(channel.Challenge);
        Assert.Equal(DouyinLoginState.Online, channel.LoginState);
    }

    // ─────────────────────────── 页面体征 ───────────────────────────

    [Fact]
    public async Task BrowserState_frame_updates_snapshot()
    {
        await using var channel = CreateChannel(DouyinDriver.Web);
        var connection = new FakeConnection();
        await channel.AttachAsync(connection);

        connection.Push(Frame(DouyinFrameTypes.BrowserState, new
        {
            page = "ok",
            memoryMb = 812,
            lastAction = "已打开私信页",
        }));
        connection.Push(PrivateInboundFrame("10086", "m-barrier", "?"));
        await ReadMessageAsync(channel, TimeSpan.FromSeconds(5));

        Assert.NotNull(channel.BrowserState);
        Assert.Equal("ok", channel.BrowserState!.Page);
        Assert.Equal(812, channel.BrowserState.MemoryMb);
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static DouyinChannel CreateChannel(string driver) =>
        new(
            new DouyinOptions
            {
                Enabled = true,
                SendTimeoutMs = 2_000,
                QueryTimeoutMs = 1_000,
                MaxTextLength = 16_000,
                Driver = driver,
            },
            new ChatFilter(new ChatFilterOptions(), DouyinChannel.PlatformName),
            Logger);

    private static string Frame(string type, object data) =>
        JsonSerializer.Serialize(new { type, time = 1_730_000_000_000L, data });

    private static string HelloFrame(string driver, string? selfId) =>
        Frame(DouyinFrameTypes.Hello, new
        {
            version = "1",
            platform = "douyin",
            driver,
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
            content = new { kind = "text", text },
        });

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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("等待条件成立超时。");
    }

    /// <summary>假的 sidecar 连接：内存双向队列（与 DouyinChannelTests 同形，独立一份避免互相牵制）。</summary>
    private sealed class FakeConnection : IDouyinConnection
    {
        private readonly System.Threading.Channels.Channel<string> _incoming =
            System.Threading.Channels.Channel.CreateUnbounded<string>();

        private readonly object _sync = new();
        private readonly List<string> _sent = [];
        private int _closed;

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

        public void Push(string json) => _incoming.Writer.TryWrite(json);
    }
}
