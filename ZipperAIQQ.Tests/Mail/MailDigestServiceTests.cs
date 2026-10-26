using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Mail;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Mails;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Mail;

/// <summary>
/// 邮件汇总转发（设计 §2.3 / 验收标准 9、10）：能不能发、失败会不会丢、会不会重复发。
/// </summary>
/// <remarks>
/// 用**真 <see cref="SqliteMailRepository"/> + 临时文件库**：本类的核心承诺是
/// 「没发出去就别标记已转发」「发出去才标记」，这正是仓储里的 <c>ForwardedAtMs</c>，
/// 假仓储证明不了这一点。库用临时文件而不是 <c>:memory:</c>，因为
/// <see cref="SqliteDatabase"/> 每次读写都开新连接（与 <c>MailRepositoryTests</c> 同理由）。
/// 假通道 / 假设置只替换外部副作用，**不打真实 QQ，也不打真实 LLM**。
/// </remarks>
public sealed class MailDigestServiceTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    /// <summary>正文里的"全文标记"：汇总文案里**不该**出现它。</summary>
    private const string BodyMarker = "正文全文-ZZZ-不该出现在汇总里";

    private readonly string _dbPath;
    private readonly ILogger _logger;
    private readonly SqliteDatabase _db;
    private readonly SqliteMailRepository _repository;

    public MailDigestServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-mail-digest-{Guid.NewGuid():N}.db");
        _logger = Logger;

        var connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(connectionString, Logger);
        _repository = new SqliteMailRepository(_db);
    }

    public async Task InitializeAsync()
    {
        var initializer = new DatabaseInitializer($"Data Source={_dbPath}", Logger);
        await initializer.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        // 连接池可能还握着句柄：删不掉就留给系统临时目录，不影响测试结论
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    // ─────────────────────────── 空转 ───────────────────────────

    [Fact]
    public async Task FlushAsync_NoPendingMails_ReturnsEmptyResultWithoutSending()
    {
        var channel = new FakeChannel();

        var result = await Service(channel, new FakeSettings()).FlushAsync(10);

        // 管道每轮都会调它：空转既不是错误，也不该刷 Warning
        Assert.Equal(0, result.MailCount);
        Assert.False(result.Sent);
        Assert.Null(result.Error);
        Assert.Empty(channel.Sent);
    }

    // ────────────────────── 主人未配置：不静默丢 ──────────────────────

    [Fact]
    public async Task FlushAsync_OwnerMissing_ReturnsReadableErrorAndKeepsPending()
    {
        var mailId = await SeedMailAsync("账单确认", "周五前确认账单金额。");

        var channel = new FakeChannel();
        var settings = new FakeSettings();      // 刻意不设 owner.userId

        var result = await Service(channel, settings).FlushAsync(10);

        Assert.Equal(1, result.MailCount);
        Assert.False(result.Sent);
        Assert.Contains("主人", result.Error);
        Assert.Empty(channel.Sent);

        // 关键：**没有标记已转发**，邮件还在待转队列里（配置主人后能补发）
        var summary = Assert.IsType<MailSummaryRow>((await _repository.GetAsync(mailId))?.Summary);
        Assert.Null(summary.ForwardedAt);
        Assert.Single(await _repository.GetPendingDigestAsync(10));
    }

    // ─────────────────────────── 发送失败 ───────────────────────────

    [Fact]
    public async Task FlushAsync_SendFails_RetriesThenKeepsPendingUnmarked()
    {
        var mailId = await SeedMailAsync("季度发票", "发票待报销。");
        var settings = new FakeSettings().Set(MailDigestService.OwnerKey, "10001");
        var channel = new FakeChannel().FailAlways();

        var result = await Service(channel, settings, Options(forwardRetryCount: 2)).FlushAsync(10);

        Assert.False(result.Sent);
        Assert.Equal(1, result.MailCount);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(3, channel.Attempts);      // 首次 + 2 次重试

        var summary = Assert.IsType<MailSummaryRow>((await _repository.GetAsync(mailId))?.Summary);
        Assert.Null(summary.ForwardedAt);
        Assert.Single(await _repository.GetPendingDigestAsync(10));
    }

    [Fact]
    public async Task FlushAsync_SendFailsOnce_RetriesAndThenMarksForwarded()
    {
        var mailId = await SeedMailAsync("面试通知", "明天上午十点面试。");
        var settings = new FakeSettings().Set(MailDigestService.OwnerKey, "10001");
        var channel = new FakeChannel().FailOnce(new InvalidOperationException("通道抖了一下"));

        var result = await Service(channel, settings).FlushAsync(10);

        Assert.True(result.Sent);
        Assert.Null(result.Error);
        Assert.Equal(2, channel.Attempts);
        Assert.Single(channel.Sent);

        var summary = Assert.IsType<MailSummaryRow>((await _repository.GetAsync(mailId))?.Summary);
        Assert.NotNull(summary.ForwardedAt);
    }

    // ─────────────────────── 成功：文案与不重复发 ───────────────────────

    [Fact]
    public async Task FlushAsync_Success_SendsDigestWithSummaryAndNeverFullBody()
    {
        await SeedMailAsync("季度发票", "发票待报销，金额 1234 元。", needsAction: true);
        var settings = new FakeSettings().Set(MailDigestService.OwnerKey, "10001");
        var channel = new FakeChannel();

        var result = await Service(channel, settings).FlushAsync(10);

        Assert.True(result.Sent);
        Assert.Equal(1, result.MailCount);
        Assert.Null(result.Error);

        var sent = Assert.Single(channel.Sent);
        Assert.Equal("qq", sent.Chat.Platform);
        Assert.Equal(ChatType.Private, sent.Chat.Type);
        Assert.Equal("10001", sent.Chat.Id);

        var text = sent.PlainText;
        Assert.Contains("季度发票", text);
        Assert.Contains("sender@example.com", text);
        Assert.Contains("发票待报销", text);
        Assert.Contains("需要处理", text);

        // 设计 §2.3：只发摘要 + 元信息，**不发正文全文**
        Assert.DoesNotContain(BodyMarker, text);
    }

    [Fact]
    public async Task FlushAsync_SecondCallAfterSuccess_SendsNothingAgain()
    {
        await SeedMailAsync("已转发过", "这封会被标记。");
        var settings = new FakeSettings().Set(MailDigestService.OwnerKey, "10001");
        var channel = new FakeChannel();
        var service = Service(channel, settings);

        var first = await service.FlushAsync(10);
        var second = await service.FlushAsync(10);

        Assert.True(first.Sent);
        Assert.Equal(0, second.MailCount);
        Assert.False(second.Sent);
        Assert.Null(second.Error);
        Assert.Single(channel.Sent);       // 只发了那一条
    }

    // ─────────────────────────── 测试数据与小工具 ───────────────────────────

    private static MailOptions Options(int forwardRetryCount = 2) => new()
    {
        Enabled = true,
        SummaryEnabled = true,
        ForwardRetryCount = forwardRetryCount,
    };

    private MailDigestService Service(IMessageChannel channel, IRuntimeSettingsStore settings, MailOptions? options = null) =>
        new FastRetryDigestService(settings, channel, _repository, options ?? Options(), _logger);

    /// <summary>塞一封"摘要成功、还没转发"的邮件，返回主键。</summary>
    private async Task<long> SeedMailAsync(
        string subject,
        string summary,
        bool needsAction = false,
        string accountId = "qq-1")
    {
        var mail = new IncomingMail
        {
            Chat = new ChatRef
            {
                Platform = "mail",
                Type = ChatType.Private,
                Id = $"{accountId}/<{Guid.NewGuid():N}@example.com>",
            },
            OccurredAt = new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(8)),
            EventId = $"<{Guid.NewGuid():N}@example.com>",
            AccountId = accountId,
            Subject = subject,
            FromAddress = "sender@example.com",
            FromName = "发件人",
            BodyText = $"{subject} 的正文。{BodyMarker}",
            HasAttachments = false,
        };

        var id = await _repository.AppendAsync(mail)
            ?? throw new InvalidOperationException("测试种子邮件没写进库");

        await _repository.SaveSummaryAsync(id, summary, needsAction, status: "ok", error: null);

        return id;
    }

    /// <summary>把退避等待压成 0：测的是分支，不是睡眠。</summary>
    private sealed class FastRetryDigestService : MailDigestService
    {
        public FastRetryDigestService(
            IRuntimeSettingsStore settings,
            IMessageChannel channel,
            IMailRepository repository,
            MailOptions options,
            ILogger logger)
            : base(settings, channel, repository, options, logger)
        {
        }

        protected override TimeSpan RetryDelay(int attempt) => TimeSpan.Zero;
    }

    /// <summary>假的运行期设置（内存 KV）。</summary>
    private sealed class FakeSettings : IRuntimeSettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public FakeSettings Set(string key, string value)
        {
            _values[key] = value;
            return this;
        }

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    /// <summary>假的通道：记录发出的消息、按脚本失败、可要求一直失败。</summary>
    private sealed class FakeChannel : IMessageChannel
    {
        private readonly Queue<Exception> _failures = new();
        private bool _alwaysFail;

        public List<OutgoingMessage> Sent { get; } = [];

        public int Attempts { get; private set; }

        public string Platform => "qq";

        public ChannelCapabilities Capabilities => ChannelCapabilities.Standard;

        public FakeChannel FailOnce(Exception exception)
        {
            _failures.Enqueue(exception);
            return this;
        }

        public FakeChannel FailAlways()
        {
            _alwaysFail = true;
            return this;
        }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public IAsyncEnumerable<InboundEvent> ReceiveAsync(CancellationToken ct) =>
            throw new NotSupportedException("测试通道不接收事件。");

        public Task SendAsync(OutgoingMessage message, CancellationToken ct)
        {
            Attempts++;

            if (_alwaysFail)
            {
                throw new InvalidOperationException("QQ 通道不可用（测试）");
            }

            if (_failures.Count > 0)
            {
                throw _failures.Dequeue();
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
