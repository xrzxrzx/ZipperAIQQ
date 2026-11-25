using Serilog;
using ZipperAIQQ.Agent.Mail;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Mail;
using ZipperAIQQ.Server.Mail;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Tests.Mail;

/// <summary>
/// 邮件双通道的服务端实现：定位 / 渲染取素材顺序 / 原文路径 / 降级口径。
/// </summary>
/// <remarks>
/// 用**真**仓储（临时文件库）配假 IMAP 与假渲染器：这几条判据全是"什么时候连服务器、
/// 什么时候用库里留底"，换成假仓储就把被测的那层替掉了。
/// </remarks>
public sealed class MailReadServiceTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly SqliteMailRepository _repo;
    private readonly FakeImapSessionFactory _imapFactory = new();
    private readonly FakeRenderer _renderer = new();

    public MailReadServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-mail-read-{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase($"Data Source={_dbPath}", Logger);
        _repo = new SqliteMailRepository(_db);
    }

    public Task InitializeAsync() =>
        new DatabaseInitializer($"Data Source={_dbPath}", Logger).InitializeAsync();

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 连接池可能还拿着句柄，删不掉不算失败
        }
    }

    // ───────────── 降级口径 ─────────────

    [Fact]
    public void CanRender_false_when_switch_is_off()
    {
        var service = CreateService(new MailOptions { RenderEnabled = false });

        Assert.False(service.CanRender);
        Assert.Contains("没开", service.RenderUnavailableReason);
    }

    [Fact]
    public void CanRender_false_when_renderer_reports_unavailable()
    {
        _renderer.IsAvailable = false;
        _renderer.UnavailableReason = "渲染脚本不存在";

        var service = CreateService(new MailOptions { RenderEnabled = true });

        Assert.False(service.CanRender);
        Assert.Contains("渲染脚本不存在", service.RenderUnavailableReason);
    }

    [Fact]
    public async Task Render_while_disabled_reports_reason_and_does_not_look_up_mail()
    {
        var id = await AppendAsync("带 HTML 的邮件", bodyHtml: "<p>x</p>");

        var outcome = await CreateService(new MailOptions { RenderEnabled = false })
            .RenderAsync(id, segments: false);

        Assert.NotNull(outcome.Error);
        Assert.Contains("没开", outcome.Error!);
        Assert.Empty(outcome.Images);
        Assert.Empty(_renderer.Renders);
    }

    // ───────────── 定位 ─────────────

    [Fact]
    public async Task Locate_by_id_and_by_keyword()
    {
        var id = await AppendAsync("银行账单", bodyHtml: "<p>x</p>");
        await AppendAsync("订阅续费");
        var service = CreateService(Enabled());

        var byId = await service.LocateAsync(id.ToString());
        Assert.Equal(id, Assert.Single(byId).Id);
        Assert.True(byId[0].HasHtml);
        Assert.True(byId[0].HasUid);

        var byKeyword = await service.LocateAsync("订阅");
        var hit = Assert.Single(byKeyword);
        Assert.Equal("订阅续费", hit.Subject);
        // 这封没 HTML → 渲染对它没意义（模型据此决定用摘要回答）
        Assert.False(hit.HasHtml);

        Assert.Empty(await service.LocateAsync("不存在的关键词"));
    }

    // ───────────── 渲染取素材 ─────────────

    [Fact]
    public async Task Render_uses_stored_html_and_never_touches_imap()
    {
        var id = await AppendAsync("带版式的邮件", bodyHtml: "<p>按钮</p>");
        var service = CreateService(Enabled());

        var outcome = await service.RenderAsync(id, segments: true);

        Assert.Null(outcome.Error);
        Assert.Single(outcome.Images);
        Assert.Equal("<p>按钮</p>", _renderer.LastHtml);
        Assert.True(_renderer.LastSegments);
        Assert.Empty(_imapFactory.Created); // 库里有留底就不该再连服务器
    }

    [Fact]
    public async Task Render_falls_back_to_imap_when_nothing_is_stored()
    {
        var id = await AppendAsync("升级前收的邮件", bodyHtml: null);
        _imapFactory.Html = "<p>服务器上的版式</p>";
        var service = CreateService(Enabled());

        var outcome = await service.RenderAsync(id, segments: false);

        Assert.Null(outcome.Error);
        Assert.Equal("<p>服务器上的版式</p>", _renderer.LastHtml);
        Assert.Single(_imapFactory.Created);
        Assert.Equal(1, _imapFactory.ConnectCount);
    }

    [Fact]
    public async Task Render_reports_plain_text_mail_instead_of_empty_picture()
    {
        var id = await AppendAsync("纯文本邮件", bodyHtml: null);
        _imapFactory.Html = null;
        var service = CreateService(Enabled());

        var outcome = await service.RenderAsync(id, segments: false);

        Assert.Contains("没有 HTML 正文", outcome.Error!);
        Assert.Empty(_renderer.Renders);
    }

    // ───────────── 原文 ─────────────

    [Fact]
    public async Task Raw_without_uid_explains_instead_of_failing_silently()
    {
        var id = await AppendAsync("没有 UID 的旧邮件", bodyHtml: null, uid: 0);
        var service = CreateService(Enabled());

        var outcome = await service.ReadRawAsync(id, maxChars: 0);

        Assert.Contains("没有记录 IMAP UID", outcome.Error!);
        Assert.Empty(_imapFactory.Created);
    }

    [Fact]
    public async Task Raw_returns_server_content_and_uses_configured_default_limit()
    {
        var id = await AppendAsync("有原文的邮件", bodyHtml: null, uid: 77);
        _imapFactory.Raw = new MailRawContent("text/html", "Subject: x", "<p>源文本</p>", Truncated: false);
        var service = CreateService(Enabled());

        var outcome = await service.ReadRawAsync(id, maxChars: 0);

        Assert.Null(outcome.Error);
        Assert.Equal("<p>源文本</p>", outcome.Body);
        Assert.Equal(77, _imapFactory.LastUid);
        // 0 = 用配置默认值
        Assert.Equal(20_000, _imapFactory.LastMaxChars);
    }

    [Fact]
    public async Task Raw_clamps_huge_limits_to_the_ceiling()
    {
        var id = await AppendAsync("超长原文", bodyHtml: null, uid: 88);
        var service = CreateService(Enabled());

        await service.ReadRawAsync(id, maxChars: int.MaxValue);

        // 配置/模型给多大都不许超过硬顶：一次把几十 MB 塞进上下文就废了
        Assert.Equal(200_000, _imapFactory.LastMaxChars);
    }

    // ───────────── 夹具 ─────────────

    private static MailOptions Enabled() => new()
    {
        RenderEnabled = true,
        RawMaxChars = 20_000,
    };

    private MailReadService CreateService(MailOptions options) => new(
        _repo,
        new MailImapAccess(
            _imapFactory, _repo, new FakeCredentialProtector(), Logger),
        _renderer,
        options,
        Logger);

    private async Task<long> AppendAsync(string subject, string? bodyHtml = null, long uid = 5)
    {
        await _repo.UpsertAccountAsync(new MailAccount
        {
            Id = "acc-1",
            Address = "bot@example.com",
            DisplayName = "收信机器人",
            ImapHost = "imap.example.com",
            ImapPort = 993,
            UseSsl = true,
            Username = "bot@example.com",
            SecretCipher = "enc:auth-code",
            Enabled = true,
        });

        var id = await _repo.AppendAsync(new IncomingMail
        {
            Chat = new ChatRef { Platform = "mail", Type = ChatType.Private, Id = "acc-1/x" },
            OccurredAt = DateTimeOffset.UtcNow,
            EventId = $"<{Guid.NewGuid():N}@example.com>",
            AccountId = "acc-1",
            Subject = subject,
            FromAddress = "sender@example.com",
            FromName = "发件人",
            BodyText = $"{subject} 的正文",
            BodyHtml = bodyHtml,
            Uid = uid,
        });

        return Assert.IsType<long>(id);
    }

    private sealed class FakeRenderer : IMailRenderer
    {
        public bool IsAvailable { get; set; } = true;

        public string UnavailableReason { get; set; } = string.Empty;

        public string? LastHtml { get; private set; }

        public bool LastSegments { get; private set; }

        public List<string> Renders { get; } = [];

        public Task<RenderedMail> RenderAsync(string html, bool segments, CancellationToken ct)
        {
            LastHtml = html;
            LastSegments = segments;
            Renders.Add(html);

            return Task.FromResult(new RenderedMail(
                [new MailRenderImage(0, [1, 2], "image/jpeg", "整封邮件")],
                "chromium",
                Truncated: false,
                PageHeight: 1200));
        }
    }

    private sealed class FakeImapSessionFactory : IImapSessionFactory
    {
        public string? Html { get; set; }

        public MailRawContent Raw { get; set; } =
            new("text/plain", "Subject: x", "正文", Truncated: false);

        public int ConnectCount { get; private set; }

        public long LastUid { get; private set; }

        public int LastMaxChars { get; private set; } = -1;

        public List<IImapSession> Created { get; } = [];

        public IImapSession Create()
        {
            var session = new FakeImapSession(this);
            Created.Add(session);
            return session;
        }

        private sealed class FakeImapSession : IImapSession
        {
            private readonly FakeImapSessionFactory _owner;

            public FakeImapSession(FakeImapSessionFactory owner) => _owner = owner;

            public Task ConnectAsync(MailAccount account, string credential, CancellationToken ct)
            {
                _owner.ConnectCount++;
                return Task.CompletedTask;
            }

            public Task<long> GetMaxUidAsync(CancellationToken ct) => Task.FromResult(0L);

            public Task<IReadOnlyList<ImapMessage>> FetchSinceAsync(
                long afterUid, int limit, CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<ImapMessage>>([]);

            public Task<string?> GetHtmlAsync(long uid, int maxChars, CancellationToken ct) =>
                Task.FromResult(_owner.Html);

            public Task<MailRawContent> GetRawAsync(long uid, int maxChars, CancellationToken ct)
            {
                _owner.LastUid = uid;
                _owner.LastMaxChars = maxChars;
                return Task.FromResult(_owner.Raw);
            }

            public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
