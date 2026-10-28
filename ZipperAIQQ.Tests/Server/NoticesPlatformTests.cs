using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Feedback;
using ZipperAIQQ.Server.Identity;
using ZipperAIQQ.Server.Notices;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Notices;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 通知与 bot 账号**按平台**（决策六 / S3-A / S3-E 前置）：
/// <c>NoticeResponder</c> 读平台键、<c>SelfIdRecorder</c> 按平台落库、<c>NoticeCatalog</c> 的抖音归属。
/// </summary>
/// <remarks>
/// <para>
/// 用**真实现**（临时文件 SQLite + <see cref="DatabaseInitializer"/> + <see cref="SqliteRuntimeSettingsStore"/>）：
/// 这一层要验的恰恰是"键到底写成了什么样、旧键还能不能读"，换成假的 KV 就把要验的东西假掉了。
/// </para>
/// <para>
/// 键名在断言里写**字面量**（<c>notice.reply:qq:Poke</c>）：这个测试同时是**键形态的契约** ——
/// 用被测代码拼期望值就变成"自己验自己"了。
/// </para>
/// </remarks>
public sealed class NoticesPlatformTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteRuntimeSettingsStore _settings;
    private readonly SqliteMessageStore _messages;

    public NoticesPlatformTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-notices-platform-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";

        _db = new SqliteDatabase(_connectionString, Logger);
        _settings = new SqliteRuntimeSettingsStore(_db);
        _messages = new SqliteMessageStore(_db);
    }

    public async Task InitializeAsync()
    {
        var initializer = new DatabaseInitializer(_connectionString, Logger);
        await initializer.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        // 连接池可能还持有句柄，忽略删除失败
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    // ─────────────────────── 读：平台键 + qq 老键回退 ───────────────────────

    [Fact]
    public async Task NoticeResponder_ReadsPlatformKey()
    {
        var responder = NewResponder();

        // 新形态：带平台段
        await _settings.SetAsync("notice.reply:qq:Poke", "1");

        // 单平台时代的老形态：只有类型段（另一种类型，避免互相干扰）
        await _settings.SetAsync("notice.reply:GroupBan", "1");

        Assert.True(await responder.IsEnabledAsync(NoticeType.Poke, "qq"));

        // 老键对 qq 仍然生效（惰性迁移：老配置不用重配）
        Assert.True(await responder.IsEnabledAsync(NoticeType.GroupBan, "qq"));

        // 老键**不回退给抖音**：老键是 QQ 建的单平台时代产物，抖音去读等于串了平台
        Assert.False(await responder.IsEnabledAsync(NoticeType.GroupBan, "douyin"));

        // 没写的键 = 关（默认值语义没变）
        Assert.False(await responder.IsEnabledAsync(NoticeType.Poke, "douyin"));
        Assert.False(await responder.IsEnabledAsync(NoticeType.MessageRecalled, "qq"));
    }

    [Fact]
    public async Task NoticeResponder_DouyinKeyIsolated()
    {
        var responder = NewResponder();

        await _settings.SetAsync("notice.reply:douyin:DouyinFriendRequest", "1");
        await _settings.SetAsync("notice.reply:qq:DouyinFriendRequest", "0");

        // 同一个类型，两个平台各读各的
        Assert.True(await responder.IsEnabledAsync(NoticeType.DouyinFriendRequest, "douyin"));
        Assert.False(await responder.IsEnabledAsync(NoticeType.DouyinFriendRequest, "qq"));
    }

    /// <summary>
    /// 走完整 <see cref="NoticeResponder.HandleAsync"/>：证明**调用点**也换成了平台键
    /// （只在入口改、内部又读旧键的话，这个用例会红）。
    /// </summary>
    [Fact]
    public async Task NoticeResponder_HandleAsync_UsesNoticeChatPlatform()
    {
        var pool = new FakeAgentPool();
        var responder = NewResponder(pool: pool);

        await _settings.SetAsync("notice.reply:douyin:DouyinMemberChanged", "1");
        await _settings.SetAsync("notice.reply:qq:DouyinMemberChanged", "0");

        // 抖音会话：开着 → 交给 Agent
        await responder.HandleAsync(Notice(NoticeType.DouyinMemberChanged, "douyin"));
        var run = Assert.Single(pool.Runs);
        Assert.Contains("抖音成员变化", run.UserText, StringComparison.Ordinal);

        // QQ 会话：同类型关着 → 只记录，不触发 Agent
        await responder.HandleAsync(Notice(NoticeType.DouyinMemberChanged, "qq"));
        Assert.Single(pool.Runs);
    }

    // ─────────────────────── SelfId：按平台落库 + 旧键 ───────────────────────

    [Fact]
    public async Task SelfIdRecorder_PersistsPerPlatform()
    {
        var channel = new FakeChannel();
        channel.SelfIds["qq"] = "10001";
        channel.SelfIds["douyin"] = "douyin-self-9527";

        var recorder = new SelfIdRecorder(channel, _settings, new SelfIdRegistry(), Logger);

        await ((IHostedService)recorder).StartAsync(CancellationToken.None);
        try
        {
            await WaitForSubscriberAsync(channel);

            // 事件签名不带平台（见 SelfIdRecorder 注释）：随便给个值，实现会把所有平台刷一遍
            channel.RaiseSelfIdChanged("10001");

            Assert.Equal("10001", await WaitForValueAsync(SelfIdRecorder.PlatformStorageKey("qq")));
            Assert.Equal("douyin-self-9527", await WaitForValueAsync(SelfIdRecorder.PlatformStorageKey("douyin")));

            // 旧键 = QQ 的值（RuntimeSettingsOverrides / GetSelfInfoAction 还在读）。
            // 这里用**字面量**读一次：键形态本身也是契约，别让 helper 改名悄悄改掉它。
            Assert.Equal("10001", await WaitForValueAsync(SelfIdRecorder.StorageKey));
            Assert.Equal("10001", await _settings.GetAsync("bot.selfId:qq"));
        }
        finally
        {
            await ((IHostedService)recorder).StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SelfIdRecorder_SkipsPlatformsWithoutSelfId()
    {
        var channel = new FakeChannel();
        channel.SelfIds["qq"] = "10001";
        channel.SelfIds["douyin"] = null;      // 抖音还没握手

        var recorder = new SelfIdRecorder(channel, _settings, new SelfIdRegistry(), Logger);

        await ((IHostedService)recorder).StartAsync(CancellationToken.None);
        try
        {
            await WaitForSubscriberAsync(channel);

            channel.RaiseSelfIdChanged("10001");

            Assert.Equal("10001", await WaitForValueAsync(SelfIdRecorder.PlatformStorageKey("qq")));
            Assert.Equal("10001", await WaitForValueAsync(SelfIdRecorder.StorageKey));

            // 空值不落库：否则重启后会把"空账号"当成已知的 selfId
            Assert.Null(await _settings.GetAsync(SelfIdRecorder.PlatformStorageKey("douyin")));
        }
        finally
        {
            await ((IHostedService)recorder).StopAsync(CancellationToken.None);
        }
    }

    // ─────────────────────── 目录：抖音归属 ───────────────────────

    [Fact]
    public void NoticeCatalog_MapsDouyinTypes()
    {
        NoticeType[] douyinTypes =
        [
            NoticeType.DouyinFriendRequest,
            NoticeType.DouyinGroupRequest,
            NoticeType.DouyinMemberChanged,
            NoticeType.DouyinSessionSynced,
        ];

        foreach (var type in douyinTypes)
        {
            Assert.Equal(NoticeCatalog.DouyinPlatform, NoticeCatalog.PlatformOf(type));
        }

        var douyin = NoticeCatalog.ForPlatform("douyin");
        Assert.NotEmpty(douyin);
        Assert.All(douyinTypes, type => Assert.Contains(type, douyin));

        // 既有 QQ 类型一个都没被搬走（口径不变），且每个类型**只归一个平台**
        Assert.Equal(NoticeCatalog.QqPlatform, NoticeCatalog.PlatformOf(NoticeType.Poke));
        Assert.DoesNotContain(NoticeType.Poke, douyin);
        Assert.Equal(
            Enum.GetValues<NoticeType>().Length,
            NoticeCatalog.ForPlatform("qq").Count + douyin.Count);
    }

    [Fact]
    public async Task GetBehavior_ListsDouyinNoticesWithPlatformAndLabel()
    {
        var action = new GetBehaviorAction(_settings);
        using var payload = JsonDocument.Parse("{}");

        var data = await action.HandleAsync(payload.RootElement, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(data, WebJson);

        // 平台下拉框：抖音现在有通知类型了，必须出现在这里
        var platforms = json.GetProperty("platforms")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToList();
        Assert.Contains("qq", platforms);
        Assert.Contains("douyin", platforms);

        var notices = json.GetProperty("notices").EnumerateArray().ToList();

        var douyinItem = notices.Single(item =>
            item.GetProperty("type").GetString() == nameof(NoticeType.DouyinFriendRequest));
        Assert.Equal("好友申请", douyinItem.GetProperty("label").GetString());
        Assert.Equal("douyin", douyinItem.GetProperty("platform").GetString());

        // 默认关：键不存在 = 关（与 QQ 侧同一个默认口径，不许替用户打开）
        Assert.All(notices, item => Assert.False(item.GetProperty("enabled").GetBoolean()));
    }

    // ─────────────────────────── 帮手 ───────────────────────────

    private NoticeResponder NewResponder(IMessageChannel? channel = null, IChatAgentPool? pool = null)
    {
        var actualChannel = channel ?? new FakeChannel();

        return new NoticeResponder(
            new FakeNoticeStore(),
            _settings,
            pool ?? new FakeAgentPool(),
            actualChannel,
            TestServerOptions(),
            new OwnerFeedbackService(_settings, _messages, actualChannel, Logger),
            new SelfIdRegistry(),
            Logger);
    }

    /// <summary>最小可用的 <see cref="ServerOptions"/>：4 个 <c>required</c> 成员必须给。</summary>
    /// <remarks>
    /// 本测试的用例都走"按会话平台读开关"与"按平台落 selfId"，
    /// 用不到自建 QQ 通道地址或 LLM 端点 —— 这些值只是为了让 <c>required</c> 过关。
    /// </remarks>
    private static ServerOptions TestServerOptions() => new()
    {
        NapCatWebSocketUrl = "ws://127.0.0.1:0",
        LlmApiKey = "test-key",
        LlmBaseUrl = "http://127.0.0.1:0",
        LlmModel = "test-model",
    };

    private static IncomingNotice Notice(NoticeType type, string platform) => new()
    {
        Chat = new ChatRef
        {
            Platform = platform,
            Type = ChatType.Private,
            Id = platform == NoticeCatalog.DouyinPlatform ? "douyin-chat-1" : "10001",
        },
        OccurredAt = new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.FromHours(8)),
        Type = type,
        RawType = type.ToString(),
        Actor = new SenderInfo { UserId = "20002", Nickname = "测试用户" },
        Target = new SenderInfo { UserId = "30003", Nickname = "另一个用户" },
    };

    /// <summary>等 recorder 真正订阅上 <c>SelfIdChanged</c>（见 <see cref="FakeChannel"/> 的说明）。</summary>
    private static async Task WaitForSubscriberAsync(FakeChannel channel, int timeoutMs = 3_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline && !channel.HasSubscribers)
        {
            await Task.Delay(10);
        }

        Assert.True(channel.HasSubscribers, "SelfIdRecorder 没有在超时内订阅 SelfIdChanged。");
    }

    /// <summary>轮询等异步落库（事件是 fire-and-forget，落库是后台任务）。</summary>
    private async Task<string?> WaitForValueAsync(string key, int timeoutMs = 3_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var value = await _settings.GetAsync(key);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await Task.Delay(20);
        }

        return await _settings.GetAsync(key);
    }

    /// <summary>假通道：两个平台、各自独立的 selfId、可手动触发 SelfIdChanged。</summary>
    /// <remarks>
    /// 事件的 add/remove 自己实现（而不是自动属性）：.NET 10 的 <see cref="BackgroundService"/>
    /// 不保证 <c>ExecuteAsync</c> 在 <c>StartAsync</c> 返回前就跑到订阅那一步，
    /// 所以测试要能**等到真的有订阅者**再触发事件，否则会偶发"事件打空、什么都没写"。
    /// </remarks>
    private sealed class FakeChannel : IMessageChannel
    {
        private Action<string>? _selfIdChanged;

        public Dictionary<string, string?> SelfIds { get; } = new(StringComparer.Ordinal)
        {
            ["qq"] = null,
            ["douyin"] = null,
        };

        public event Action<string>? SelfIdChanged
        {
            add
            {
                _selfIdChanged += value;
                SubscriberCount++;
            }

            remove
            {
                _selfIdChanged -= value;
                SubscriberCount--;
            }
        }

        /// <summary>当前订阅者数量（&gt; 0 表示 recorder 已经接上）。</summary>
        public int SubscriberCount { get; private set; }

        public bool HasSubscribers => SubscriberCount > 0;

        public string Platform => NoticeCatalog.QqPlatform;

        public string? SelfId => SelfIds[NoticeCatalog.QqPlatform];

        public ChannelCapabilities Capabilities => ChannelCapabilities.Standard;

        public IReadOnlyList<string> Platforms => [NoticeCatalog.QqPlatform, NoticeCatalog.DouyinPlatform];

        public string? SelfIdFor(string platform) =>
            SelfIds.TryGetValue(platform, out var selfId) ? selfId : null;

        public void RaiseSelfIdChanged(string selfId) => _selfIdChanged?.Invoke(selfId);

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public IAsyncEnumerable<InboundEvent> ReceiveAsync(CancellationToken ct) =>
            throw new NotSupportedException("测试通道不接收事件。");

        public Task SendAsync(OutgoingMessage message, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>假通知仓储：只需能落库，测试不看内容。</summary>
    private sealed class FakeNoticeStore : INoticeStore
    {
        public List<IncomingNotice> Appended { get; } = [];

        public Task<long> AppendAsync(IncomingNotice notice, CancellationToken ct = default)
        {
            Appended.Add(notice);
            return Task.FromResult((long)Appended.Count);
        }

        public Task<IReadOnlyList<StoredNotice>> ListRecentAsync(
            string sessionId,
            int limit,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredNotice>>([]);

        public Task<int> CountAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    /// <summary>假 agent 池：把收到的请求记下来（用来证明"有没有真的去响应"）。</summary>
    private sealed class FakeAgentPool : IChatAgentPool
    {
        public List<AgentRunRequest> Runs { get; } = [];

        public int LoadedCount => Runs.Count;

        public IChatAgent GetOrCreate(ChatRef chat) => new FakeAgent(chat.SessionId, Runs);
    }

    private sealed class FakeAgent : IChatAgent
    {
        private readonly List<AgentRunRequest> _runs;

        public FakeAgent(string sessionId, List<AgentRunRequest> runs)
        {
            SessionId = sessionId;
            _runs = runs;
        }

        public string SessionId { get; }

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
        {
            _runs.Add(request);
            return Task.FromResult(new AgentRunResult(null, 0));
        }

        public void ResetHistory()
        {
        }

        public void RememberCommand(string userMessage, string commandResponse)
        {
        }
    }
}
