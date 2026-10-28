using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Tests.Channels;

/// <summary>
/// 复合通道（S3-A / 决策三）：入站合并、出站按平台路由、故障隔离、按平台 selfId。
/// </summary>
/// <remarks>
/// ⚠️ 本文件的命名空间是 <c>ZipperAIQQ.Tests.Channels</c>，而项目里有个 <c>ZipperAIQQ.Channel</c> 命名空间 ——
/// 裸写 <c>Channel.CreateUnbounded</c> 会被解析到那个命名空间（CS0234），所以静态调用一律写全
/// <c>System.Threading.Channels.Channel</c>。这个坑本项目已经踩过三次。
/// </remarks>
public sealed class CompositeMessageChannelTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    // ─────────────────────────── 入站合并 ───────────────────────────

    [Fact]
    public async Task ReceiveAsync_TwoChannels_MergesEventsFromBoth()
    {
        // Arrange
        var qq = new FakeChannel("qq");
        var douyin = new FakeChannel("douyin");
        var composite = new CompositeMessageChannel([qq, douyin], Logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        var reader = ReadAsync(composite, expected: 2, cts.Token);
        await qq.PushAsync(Message("qq", "p1"));
        await douyin.PushAsync(Message("douyin", "p2"));
        var events = await reader;

        // Assert
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Chat.Platform == "qq");
        Assert.Contains(events, e => e.Chat.Platform == "douyin");
    }

    [Fact]
    public async Task ReceiveAsync_SingleChannel_PassesEventsThroughUnchanged()
    {
        // 只注册一个通道时必须与"直接用该通道"完全一致（回归门槛）
        var qq = new FakeChannel("qq");
        var composite = new CompositeMessageChannel([qq], Logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var reader = ReadAsync(composite, expected: 1, cts.Token);
        var original = Message("qq", "p1");
        await qq.PushAsync(original);
        var events = await reader;

        Assert.Same(original, events[0]);
    }

    [Fact]
    public async Task ReceiveAsync_OneChannelFaults_OtherKeepsFlowing()
    {
        // 验收：一个平台断了，其他平台必须继续（杀 sidecar 不影响 QQ）
        var broken = new FakeChannel("douyin", throwOnReceive: true);
        var qq = new FakeChannel("qq");
        var composite = new CompositeMessageChannel([qq, broken], Logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var reader = ReadAsync(composite, expected: 1, cts.Token);
        await qq.PushAsync(Message("qq", "p1"));
        var events = await reader;

        Assert.Single(events);
        Assert.Equal("qq", events[0].Chat.Platform);
    }

    // ─────────────────────────── 出站路由 ───────────────────────────

    [Fact]
    public async Task SendAsync_RoutesByChatPlatform()
    {
        var qq = new FakeChannel("qq");
        var douyin = new FakeChannel("douyin");
        var composite = new CompositeMessageChannel([qq, douyin], Logger);

        await composite.SendAsync(Outgoing("qq", "1"), CancellationToken.None);
        await composite.SendAsync(Outgoing("douyin", "2"), CancellationToken.None);

        Assert.Single(qq.Sent);
        Assert.Equal("qq:g1", qq.Sent[0].Chat.SessionId);
        Assert.Single(douyin.Sent);
        Assert.Equal("douyin:g2", douyin.Sent[0].Chat.SessionId);
    }

    [Fact]
    public async Task SendAsync_UnknownPlatform_ThrowsWithRegisteredPlatforms()
    {
        // 静默丢弃会让模型以为发出去了（FR-2.3），所以必须明确抛
        var composite = new CompositeMessageChannel([new FakeChannel("qq")], Logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composite.SendAsync(Outgoing("telegram", "x1"), CancellationToken.None));

        Assert.Contains("telegram", ex.Message, StringComparison.Ordinal);
        Assert.Contains("qq", ex.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────── selfId / 能力 / 平台表 ───────────────────────────

    [Fact]
    public void SelfIdFor_ReturnsPerPlatformSelfId()
    {
        var composite = new CompositeMessageChannel(
            [new FakeChannel("qq", selfId: "10001"), new FakeChannel("douyin", selfId: "dy-9")],
            Logger);

        Assert.Equal("10001", composite.SelfIdFor("qq"));
        Assert.Equal("dy-9", composite.SelfIdFor("douyin"));
        Assert.Null(composite.SelfIdFor("unknown"));

        // 兼容旧单值消费者：Platform / SelfId 取第一个通道
        Assert.Equal("qq", composite.Platform);
        Assert.Equal("10001", composite.SelfId);
    }

    [Fact]
    public void Platforms_ListsRegisteredPlatformsInOrder()
    {
        var composite = new CompositeMessageChannel(
            [new FakeChannel("qq"), new FakeChannel("douyin")], Logger);

        Assert.Equal(["qq", "douyin"], composite.Platforms);
    }

    [Fact]
    public void Capabilities_IsUnionOfChildren()
    {
        var composite = new CompositeMessageChannel(
            [
                new FakeChannel("qq", capabilities: ChannelCapabilities.Text | ChannelCapabilities.Image),
                new FakeChannel("douyin", capabilities: ChannelCapabilities.Text | ChannelCapabilities.Reply),
            ],
            Logger);

        Assert.Equal(
            ChannelCapabilities.Text | ChannelCapabilities.Image | ChannelCapabilities.Reply,
            composite.Capabilities);
    }

    // ─────────────────────────── 构造与启动的失败姿势 ───────────────────────────

    [Fact]
    public void Constructor_NoChannels_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CompositeMessageChannel([], Logger));
    }

    [Fact]
    public void Constructor_DuplicatePlatform_Throws()
    {
        // 同平台两个实现 ⇒ 出站路由变成掷骰子
        var ex = Assert.Throws<ArgumentException>(
            () => new CompositeMessageChannel([new FakeChannel("qq"), new FakeChannel("qq")], Logger));

        Assert.Contains("qq", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_OneChildThrows_OtherStillStarted()
    {
        var broken = new FakeChannel("douyin", throwOnStart: true);
        var qq = new FakeChannel("qq");
        var composite = new CompositeMessageChannel([qq, broken], Logger);

        await composite.StartAsync(CancellationToken.None);

        Assert.Equal(1, qq.StartCount);
    }

    [Fact]
    public async Task ContactDirectory_ForwardsToPrimaryChannel()
    {
        // ChatAgent 靠 `_channel is IContactDirectory` 决定要不要给模型联系人工具，
        // 所以复合通道必须仍然"是"一个 IContactDirectory（转发给主平台）。
        var contactDirectory = new FakeChannel("qq");
        var composite = new CompositeMessageChannel([contactDirectory], Logger);

        var friends = await ((IContactDirectory)composite).GetFriendsAsync();

        Assert.Single(friends);
        Assert.Equal("u1", friends[0].UserId);
    }

    // ─────────────────────────── 测试替身 ───────────────────────────

    private sealed class FakeChannel : IMessageChannel, IContactDirectory
    {
        private readonly Channel<InboundEvent> _inbound =
            System.Threading.Channels.Channel.CreateUnbounded<InboundEvent>();

        private readonly bool _throwOnReceive;
        private readonly bool _throwOnStart;
        private readonly string? _selfId;
        private readonly ChannelCapabilities _capabilities;

        public FakeChannel(
            string platform,
            string? selfId = null,
            ChannelCapabilities capabilities = ChannelCapabilities.Text,
            bool throwOnReceive = false,
            bool throwOnStart = false)
        {
            Platform = platform;
            _selfId = selfId;
            _capabilities = capabilities;
            _throwOnReceive = throwOnReceive;
            _throwOnStart = throwOnStart;
        }

        public string Platform { get; }

        public string? SelfId => _selfId;

        public ChannelCapabilities Capabilities => _capabilities;

        public int StartCount { get; private set; }

        public List<OutgoingMessage> Sent { get; } = [];

        public Task StartAsync(CancellationToken ct = default)
        {
            if (_throwOnStart)
            {
                throw new InvalidOperationException($"{Platform} 启动失败（测试注入）");
            }

            StartCount++;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_throwOnReceive)
            {
                throw new InvalidOperationException($"{Platform} 接收流断了（测试注入）");
            }

            await foreach (var evt in _inbound.Reader.ReadAllAsync(ct))
            {
                yield return evt;
            }
        }

        public Task SendAsync(OutgoingMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task PushAsync(InboundEvent evt) => _inbound.Writer.WriteAsync(evt).AsTask();

        // ── IContactDirectory：只需"能被转发"，返回值固定在测试里断言 ──
        public Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContactInfo>>([new ContactInfo("u1", "好友", null)]);

        public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GroupInfo>>([]);

        public Task SetSignatureAsync(string text, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendPokeAsync(string userId, string? groupId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<GroupInfo?> GetGroupAsync(string groupId, CancellationToken ct = default) =>
            Task.FromResult<GroupInfo?>(null);

        public Task<IReadOnlyList<GroupMemberInfo>> GetGroupMembersAsync(
            string groupId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GroupMemberInfo>>([]);

        public Task<System.Text.Json.Nodes.JsonNode?> InvokeApiAsync(
            string action, System.Text.Json.Nodes.JsonObject? parameters, CancellationToken ct = default) =>
            Task.FromResult<System.Text.Json.Nodes.JsonNode?>(null);
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static async Task<List<InboundEvent>> ReadAsync(
        CompositeMessageChannel composite, int expected, CancellationToken ct)
    {
        var events = new List<InboundEvent>();
        await foreach (var evt in composite.ReceiveAsync(ct))
        {
            events.Add(evt);
            if (events.Count >= expected)
            {
                break;
            }
        }

        return events;
    }

    private static IncomingMessage Message(string platform, string chatId) => new()
    {
        Chat = new ChatRef { Platform = platform, Type = ChatType.Private, Id = chatId },
        Sender = new SenderInfo { UserId = "u1", Nickname = "测试" },
        Segments = [new TextSegment("hi")],
        OccurredAt = DateTimeOffset.UnixEpoch,
        EventId = Guid.NewGuid().ToString("N"),
    };

    private static OutgoingMessage Outgoing(string platform, string chatId) => new()
    {
        Chat = new ChatRef { Platform = platform, Type = ChatType.Group, Id = chatId },
        Segments = [new TextSegment("hello")],
    };
}
