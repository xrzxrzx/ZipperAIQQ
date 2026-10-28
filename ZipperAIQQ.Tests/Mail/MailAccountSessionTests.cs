using System.Threading.Channels;
using MailKit.Security;
using Serilog;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Mail;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Tests.Mail;

/// <summary>
/// 邮件收信会话：多账号隔离 / 去重与水位 / 首次同步窗口 / 事件形状 / 热重建。
/// </summary>
/// <remarks>
/// <para>
/// 用**真** <see cref="SqliteMailRepository"/>（临时文件库）：去重与水位是落库行为，
/// 用假仓储就等于把被测的那层逻辑也替掉了 —— 「AppendAsync 返回 null」这条断言语义
/// 只有在真唯一索引下才有意义。
/// </para>
/// <para>
/// IMAP 那一侧用假会话工厂（编码规范 §13：外部依赖用接口 + 假实现，单测不打真实 IMAP）。
/// </para>
/// </remarks>
public sealed class MailAccountSessionTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly SqliteMailRepository _repo;

    /// <summary>本测试的「服务器脚本」：账号 ID → 假会话行为。</summary>
    private readonly Dictionary<string, SessionScript> _scripts = new(StringComparer.Ordinal);

    public MailAccountSessionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-mail-session-{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase($"Data Source={_dbPath}", Logger);
        _repo = new SqliteMailRepository(_db);
    }

    public Task InitializeAsync() =>
        new DatabaseInitializer($"Data Source={_dbPath}", Logger).InitializeAsync();

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        // 连接池可能还持有句柄，删不掉就算了（临时目录）
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    // ─────────────────────────── 用例 ───────────────────────────

    [Fact]
    public async Task RunAsync_OneAccountAuthFails_OtherAccountKeepsReceiving()
    {
        // Arrange：A 的授权码失效，B 正常，且在 A 死掉之后还能收到第二封
        _scripts["acc-a"] = new SessionScript
        {
            ConnectException = new AuthenticationException("535 Login fail"),
        };

        var bScript = new SessionScript { MaxUid = 1 };
        bScript.Pages.Enqueue([Message(1, "<b-1@example.com>")]);
        bScript.Pages.Enqueue([Message(2, "<b-2@example.com>")]);
        _scripts["acc-b"] = bScript;

        await _repo.UpsertAccountAsync(Account("acc-a"));
        await _repo.UpsertAccountAsync(Account("acc-b"));

        var factory = new FakeImapSessionFactory(_scripts);
        var source = new MailSource(
            factory,
            _repo,
            new FakeCredentialProtector(),
            FastPollOptions(),
            Logger);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // Act：A 停掉之后，B 仍然连收两封
            var first = await ReadMailAsync(source, TimeSpan.FromSeconds(10));
            var second = await ReadMailAsync(source, TimeSpan.FromSeconds(10));

            // Assert
            Assert.Equal("acc-b", first.AccountId);
            Assert.Equal("acc-b", second.AccountId);

            var failed = await WaitForAccountAsync(
                "acc-a",
                account => account.LastError is not null,
                TimeSpan.FromSeconds(5));

            var lastError = failed.LastError ?? string.Empty;
            Assert.Contains("认证失败", lastError);
            Assert.DoesNotContain("super-secret", lastError); // 凭据绝不进 LastError
            Assert.Equal(0, failed.LastUid);

            var accountA = factory.Created.Single(session => session.AccountId == "acc-a");
            Assert.Equal(1, accountA.ConnectCount); // 认证失败**不重试**
            Assert.True(accountA.Disposed);

            var accountB = factory.Created.Single(session => session.AccountId == "acc-b");
            Assert.False(accountB.Disposed);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SyncOnceAsync_ServerReplaysStaleUids_DoesNotSpinOrAdvance()
    {
        // 实测 163 会无视 UID 范围、把旧的一页原样重投：水位卡死 → 每分钟重拉一次，
        // 还顺带把连接拖到被掐（2026-10-28 线上现象）。这里锁住"看到旧 UID 就本分收手"。
        var account = Account("acc-stale", lastUid: 5);
        await _repo.UpsertAccountAsync(account);

        _scripts["acc-stale"] = new SessionScript
        {
            MaxUid = 5,
            Replay = true,                       // 无视水位，永远返回同一封（uid=5 = 水位本身）
            Always = [Message(5, "<already@example.com>")],
        };

        var (session, events) = NewSession(account);
        await using var sessionScope = session;

        var processed = await session.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Empty(Drain(events));
        Assert.Equal(5, (await _repo.LoadAccountsAsync()).Single().LastUid); // 水位不动（也没有更旧的被拉进来）
    }

    [Fact]
    public async Task SyncOnceAsync_Success_ClearsPreviousErrorState()
    {
        // 上一轮失败写了 LastError，这一轮好了就必须清掉 —— 否则卡片永远红着旧账
        var account = Account("acc-err", lastUid: 3) with { LastError = "IOException: 远程主机强迫关闭了一个现有的连接。" };
        await _repo.UpsertAccountAsync(account);

        _scripts["acc-err"] = new SessionScript { MaxUid = 3, Always = [] };

        var (session, _) = NewSession(account);
        await using var sessionScope = session;

        await session.SyncOnceAsync(CancellationToken.None);

        Assert.Null((await _repo.LoadAccountsAsync()).Single().LastError);
    }

    [Fact]
    public async Task SyncOnceAsync_DuplicateDelivery_SkipsEventButAdvancesWatermark()
    {
        // Arrange：库里已有 uid=2 那封（模拟"落库成功但水位没来得及写"的崩溃窗口），
        // 服务器重投同一封。
        var account = Account("acc-a", lastUid: 1);
        await _repo.UpsertAccountAsync(account);

        var duplicate = Message(2, "<dup@example.com>");
        var inserted = await _repo.AppendAsync(StoredMail("acc-a", duplicate));
        Assert.True(inserted is not null);

        _scripts["acc-a"] = new SessionScript
        {
            MaxUid = 2,
            Replay = true,
            Always = [duplicate],
        };

        var (session, events) = NewSession(account);
        await using var sessionScope = session;

        // Act
        var processed = await session.SyncOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, processed);                 // 没有新增
        Assert.Empty(Drain(events));                // 不发事件（否则摘要 / QQ 转发会重放刷屏）

        var stored = (await _repo.LoadAccountsAsync()).Single();
        Assert.Equal(2, stored.LastUid);            // 但**要**推水位：否则每 60 秒重拉同一页、水位卡死

        Assert.Single(await _repo.ListAsync("acc-a", limit: 10)); // 也没有第二行
    }

    [Fact]
    public async Task SyncOnceAsync_FirstSync_OnlyKeepsMessagesWithinInitialDays()
    {
        // Arrange：一封 30 天前、两封在窗口内（InitialDays = 7）
        var old = Message(1, "<old@example.com>", sentAt: DateTimeOffset.UtcNow.AddDays(-30));
        var recent1 = Message(2, "<recent-1@example.com>", sentAt: DateTimeOffset.UtcNow.AddDays(-2));
        var recent2 = Message(3, "<recent-2@example.com>", sentAt: DateTimeOffset.UtcNow.AddHours(-3));

        _scripts["acc-a"] = new SessionScript
        {
            MaxUid = 3,
            Always = [old, recent1, recent2],
        };

        var account = Account("acc-a");
        await _repo.UpsertAccountAsync(account);

        var (session, events) = NewSession(account);
        await using var sessionScope = session;

        // Act
        var processed = await session.SyncOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, processed);
        Assert.Equal(
            new[] { "<recent-1@example.com>", "<recent-2@example.com>" },
            Drain(events).Select(mail => mail.EventId));

        Assert.Equal(2, (await _repo.ListAsync("acc-a", limit: 10)).Count);
        Assert.Equal(3, (await _repo.LoadAccountsAsync()).Single().LastUid);
    }

    [Fact]
    public async Task SyncOnceAsync_NewMail_EmitsExpectedEventShape()
    {
        // Arrange
        var sentAt = new DateTimeOffset(2026, 10, 26, 8, 30, 0, TimeSpan.FromHours(8));
        var message = Message(
            7,
            "<shape@example.com>",
            subject: "月度对账单",
            sentAt: sentAt,
            body: "请查收",
            hasAttachments: true);

        _scripts["acc-a"] = new SessionScript { MaxUid = 7, Always = [message] };

        var account = Account("acc-a");
        await _repo.UpsertAccountAsync(account);

        var (session, events) = NewSession(account);
        await using var sessionScope = session;

        // Act
        await session.SyncOnceAsync(CancellationToken.None);

        // Assert
        var mail = Assert.Single(Drain(events));
        Assert.Equal("mail", mail.Chat.Platform);
        Assert.Equal(ChatType.Private, mail.Chat.Type);
        Assert.Equal("acc-a/<shape@example.com>", mail.Chat.Id);
        Assert.Equal("<shape@example.com>", mail.EventId);
        Assert.Equal(sentAt, mail.OccurredAt);
        Assert.Equal("acc-a", mail.AccountId);
        Assert.Equal("月度对账单", mail.Subject);
        Assert.Equal("sender@example.com", mail.FromAddress);
        Assert.Equal("发件人", mail.FromName);
        Assert.Equal("请查收", mail.BodyText);
        Assert.True(mail.HasAttachments);
    }

    [Fact]
    public async Task SyncOnceAsync_LongBody_TruncatesWithoutSplittingSurrogatePair()
    {
        // Arrange：正文 "ab😀cd"，上限 3 —— 第 3 个 char 是 emoji 的高代理，必须退一格
        var message = Message(1, "<emoji@example.com>", body: "ab😀cd");
        _scripts["acc-a"] = new SessionScript { MaxUid = 1, Always = [message] };

        var account = Account("acc-a");
        await _repo.UpsertAccountAsync(account);

        var (session, events) = NewSession(account, options: new MailSourceOptions
        {
            InitialDays = 7,
            PollIntervalSeconds = 60,
            MaxBodyChars = 3,
        });
        await using var sessionScope = session;

        // Act
        await session.SyncOnceAsync(CancellationToken.None);

        // Assert
        var mail = Assert.Single(Drain(events));
        Assert.Equal("ab", mail.BodyText);
        Assert.False(char.IsHighSurrogate(mail.BodyText[^1]));
    }

    [Fact]
    public async Task ReloadAsync_AccountAddedAndRemoved_ConnectsNewAndStopsRemoved()
    {
        // Arrange：先只有 A
        _scripts["acc-a"] = Script(maxUid: 1, [Message(1, "<a-1@example.com>")]);
        _scripts["acc-b"] = Script(maxUid: 1, [Message(1, "<b-1@example.com>")]);

        await _repo.UpsertAccountAsync(Account("acc-a"));

        var factory = new FakeImapSessionFactory(_scripts);
        var source = new MailSource(
            factory,
            _repo,
            new FakeCredentialProtector(),
            FastPollOptions(),
            Logger);

        await source.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("acc-a", (await ReadMailAsync(source, TimeSpan.FromSeconds(10))).AccountId);

            // Act：删 A、加 B，然后热重建（不重启服务）
            await _repo.DeleteAccountAsync("acc-a");
            await _repo.UpsertAccountAsync(Account("acc-b"));
            await source.ReloadAsync();

            // Assert：B 连上了、A 被停掉了
            Assert.Equal("acc-b", (await ReadMailAsync(source, TimeSpan.FromSeconds(10))).AccountId);

            var removed = factory.Created.Single(session => session.AccountId == "acc-a");
            Assert.True(removed.Disconnected || removed.Disposed);

            var added = factory.Created.Single(session => session.AccountId == "acc-b");
            Assert.Equal(1, added.ConnectCount);
            Assert.False(added.Disposed);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    // ─────────────────────────── 夹具 ───────────────────────────

    private static MailSourceOptions FastPollOptions() => new()
    {
        InitialDays = 7,
        PollIntervalSeconds = 1, // 隔离用例要等第二轮轮询，别用默认 60s
        MaxBodyChars = 20000,
    };

    private (MailAccountSession Session, ChannelReader<InboundEvent> Events) NewSession(
        MailAccount account,
        string credential = "super-secret-auth-code",
        MailSourceOptions? options = null)
    {
        // 写全 System.Threading.Channels.Channel：ZipperAIQQ.Tests 下还有个 Channel 命名空间
        var channel = System.Threading.Channels.Channel.CreateUnbounded<InboundEvent>();
        var session = new MailAccountSession(
            account,
            credential,
            new FakeImapSessionFactory(_scripts),
            _repo,
            options ?? FastPollOptions(),
            channel.Writer,
            Logger);

        return (session, channel.Reader);
    }

    private static MailAccount Account(string id, long lastUid = 0) => new()
    {
        Id = id,
        Address = $"{id}@example.com",
        DisplayName = id,
        ImapHost = "imap.example.com",
        ImapPort = 993,
        UseSsl = true,
        Username = $"{id}@example.com",
        SecretCipher = $"enc:super-secret-{id}",
        Enabled = true,
        LastUid = lastUid,
    };

    private static ImapMessage Message(
        long uid,
        string? messageId = null,
        string subject = "测试邮件",
        DateTimeOffset? sentAt = null,
        string body = "正文",
        bool hasAttachments = false) => new(
            Uid: uid,
            MessageId: messageId ?? $"<msg-{uid}@example.com>",
            Subject: subject,
            FromAddress: "sender@example.com",
            FromName: "发件人",
            BodyText: body,
            HasAttachments: hasAttachments,
            SentAt: sentAt ?? DateTimeOffset.UtcNow);

    /// <summary>按事件形状落库一封（用于制造「已存在」）。</summary>
    private static IncomingMail StoredMail(string accountId, ImapMessage message) => new()
    {
        Chat = new ChatRef
        {
            Platform = MailSource.PlatformName,
            Type = ChatType.Private,
            Id = $"{accountId}/{message.MessageId}",
        },
        OccurredAt = message.SentAt,
        EventId = message.MessageId,
        AccountId = accountId,
        Subject = message.Subject,
        FromAddress = message.FromAddress,
        FromName = message.FromName,
        BodyText = message.BodyText,
        HasAttachments = message.HasAttachments,
    };

    private static SessionScript Script(long maxUid, IReadOnlyList<ImapMessage> page)
    {
        var script = new SessionScript { MaxUid = maxUid };
        script.Pages.Enqueue(page);
        return script;
    }

    /// <summary>把通道里已经写出的事件全部取出来（无界通道写入是同步完成的）。</summary>
    private static List<IncomingMail> Drain(ChannelReader<InboundEvent> reader)
    {
        var mails = new List<IncomingMail>();
        while (reader.TryRead(out var inboundEvent))
        {
            if (inboundEvent is IncomingMail mail)
            {
                mails.Add(mail);
            }
        }

        return mails;
    }

    /// <summary>从 <see cref="MailSource"/> 的事件流里读下一封（超时即失败）。</summary>
    private static async Task<IncomingMail> ReadMailAsync(MailSource source, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var inboundEvent in source.ReceiveAsync(cts.Token))
            {
                if (inboundEvent is IncomingMail mail)
                {
                    return mail;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"等待邮件事件超时（{timeout.TotalSeconds:0.#}s）。");
        }

        throw new InvalidOperationException("邮件事件流已结束，等不到事件。");
    }

    /// <summary>等账号状态满足条件（会话是后台任务，状态落库有先后）。</summary>
    private async Task<MailAccount> WaitForAccountAsync(
        string accountId,
        Func<MailAccount, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        MailAccount? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = (await _repo.LoadAccountsAsync()).Single(account => account.Id == accountId);
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"等待账号 {accountId} 状态超时（LastError={last?.LastError ?? "null"}）。");
    }

    // ─────────────────────────── 假实现 ───────────────────────────

    /// <summary>一个账号的「服务器行为」脚本。</summary>
    private sealed class SessionScript
    {
        /// <summary>连接时抛出的异常（模拟认证失败）。</summary>
        public Exception? ConnectException { get; init; }

        /// <summary>服务器当前最大 UID（首次同步对齐水位用）。</summary>
        public long MaxUid { get; init; }

        /// <summary>true = 无视水位重复投递（模拟服务器/客户端重投）。</summary>
        public bool Replay { get; init; }

        /// <summary>每次都返回同一页。</summary>
        public IReadOnlyList<ImapMessage>? Always { get; init; }

        /// <summary>按轮次依次返回的页（取空后返回空页）。</summary>
        public Queue<IReadOnlyList<ImapMessage>> Pages { get; } = new();
    }

    private sealed class FakeImapSessionFactory : IImapSessionFactory
    {
        private readonly IReadOnlyDictionary<string, SessionScript> _scripts;

        public FakeImapSessionFactory(IReadOnlyDictionary<string, SessionScript> scripts)
        {
            _scripts = scripts;
        }

        public List<FakeImapSession> Created { get; } = [];

        public IImapSession Create()
        {
            var session = new FakeImapSession(_scripts);
            Created.Add(session);
            return session;
        }
    }

    private sealed class FakeImapSession : IImapSession
    {
        private readonly IReadOnlyDictionary<string, SessionScript> _scripts;
        private SessionScript? _script;

        public FakeImapSession(IReadOnlyDictionary<string, SessionScript> scripts)
        {
            _scripts = scripts;
        }

        /// <summary>连接后才知道是哪个账号（工厂的 Create 不带参数）。</summary>
        public string? AccountId { get; private set; }

        public int ConnectCount { get; private set; }

        public bool Disconnected { get; private set; }

        public bool Disposed { get; private set; }

        public Task ConnectAsync(MailAccount account, string credential, CancellationToken ct)
        {
            ConnectCount++;
            AccountId = account.Id;
            _script = _scripts[account.Id];

            if (_script.ConnectException is { } failure)
            {
                throw failure;
            }

            return Task.CompletedTask;
        }

        public Task<long> GetMaxUidAsync(CancellationToken ct) =>
            Task.FromResult(_script?.MaxUid ?? 0);

        public Task<IReadOnlyList<ImapMessage>> FetchSinceAsync(
            long afterUid,
            int limit,
            CancellationToken ct)
        {
            var script = _script ?? throw new InvalidOperationException("会话尚未连接");

            IReadOnlyList<ImapMessage> page;
            if (script.Always is { } always)
            {
                page = always;
            }
            else if (script.Pages.Count > 0)
            {
                page = script.Pages.Dequeue();
            }
            else
            {
                page = [];
            }

            if (!script.Replay && afterUid > 0)
            {
                page = page.Where(message => message.Uid > afterUid).ToArray();
            }

            if (page.Count > limit)
            {
                page = page.Take(limit).ToArray();
            }

            return Task.FromResult(page);
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            Disconnected = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCredentialProtector : ICredentialProtector
    {
        public string Protect(string plaintext) => $"enc:{plaintext}";

        public string? Unprotect(string? cipher) =>
            cipher is not null && cipher.StartsWith("enc:", StringComparison.Ordinal)
                ? cipher[4..]
                : null;
    }
}
