using Microsoft.Data.Sqlite;
using Serilog;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>
/// 邮件仓储：去重 / 列表 / 摘要 / 转发 / 账号生命周期 / 并发写。
/// </summary>
/// <remarks>
/// 用**临时文件**库而不是 <c>:memory:</c>：<see cref="SqliteDatabase"/> 每次读写都开新连接，
/// 内存库的生命周期跟着连接走，关掉就没了。
/// </remarks>
public sealed class MailRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteMailRepository _repo;

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    public MailRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-mail-test-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _repo = new SqliteMailRepository(_db);
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

    // ─────────────────────────── 测试数据 ───────────────────────────

    private static IncomingMail Mail(
        string subject = "测试邮件",
        string accountId = "acc-1",
        string? messageId = null,
        DateTimeOffset? sentAt = null)
        => new()
        {
            Chat = new ChatRef { Platform = "mail", Type = ChatType.Private, Id = accountId },
            OccurredAt = sentAt ?? DateTimeOffset.UtcNow,
            // 不显式给 Message-Id 时每封都不同：测试要能区分「去重命中」与「本来就没 ID」
            EventId = messageId ?? $"<{Guid.NewGuid():N}@example.com>",
            AccountId = accountId,
            Subject = subject,
            FromAddress = "sender@example.com",
            FromName = "发件人",
            BodyText = $"{subject} 的正文",
            HasAttachments = false,
        };

    private static MailAccount Account(
        string id = "acc-1",
        string address = "bot@example.com",
        bool enabled = true)
        => new()
        {
            Id = id,
            Address = address,
            DisplayName = "收信机器人",
            ImapHost = "imap.example.com",
            ImapPort = 993,
            UseSsl = true,
            Username = address,
            SecretCipher = "cipher-v1",
            Enabled = enabled,
        };

    /// <summary>
    /// 断言追加成功并取出主键。
    /// </summary>
    /// <remarks>
    /// 直接写 <c>id.Value</c> 会触发 CS8629（可空值类型的 .Value），
    /// 本测试项目开了 <c>TreatWarningsAsErrors</c>，所以统一走这里。
    /// </remarks>
    private static long RequireId(long? id)
    {
        Assert.True(id.HasValue, "追加应返回主键；返回 null 表示这条邮件已存在");
        return id.GetValueOrDefault();
    }

    /// <summary>绕过仓储直接数行 —— 用来验证「没留下孤儿行」这类不变量。</summary>
    private Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
        => _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(result);
        });

    // ─────────────────────────── 追加 / 去重 ───────────────────────────

    [Fact]
    public async Task AppendAsync_NewMail_ReturnsPrimaryKey()
    {
        var id = await _repo.AppendAsync(Mail("第一封"));

        Assert.True(RequireId(id) > 0);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Mails;"));
    }

    [Fact]
    public async Task AppendAsync_DuplicateMessageId_ReturnsNullAndKeepsOneRow()
    {
        var mail = Mail("重复投递", messageId: "<dup-1@example.com>");

        var first = await _repo.AppendAsync(mail);
        var second = await _repo.AppendAsync(mail);

        Assert.True(RequireId(first) > 0);
        Assert.False(second.HasValue, "同一 (AccountId, MessageId) 第二次应返回 null");
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Mails;"));
    }

    [Fact]
    public async Task AppendAsync_SameMessageIdInOtherAccount_StoredSeparately()
    {
        const string messageId = "<shared@example.com>";

        var forA = await _repo.AppendAsync(Mail("A 账号收到", accountId: "acc-a", messageId: messageId));
        var forB = await _repo.AppendAsync(Mail("B 账号收到", accountId: "acc-b", messageId: messageId));

        Assert.NotEqual(RequireId(forA), RequireId(forB));
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM Mails;"));
    }

    [Fact]
    public async Task AppendAsync_WithoutMessageId_AllowsRepeatedMail()
    {
        // 平台没给 Message-Id 时不做去重（唯一索引只约束非 NULL）
        var first = await _repo.AppendAsync(Mail("无 ID 第一封", messageId: null));
        var second = await _repo.AppendAsync(Mail("无 ID 第二封", messageId: null));

        Assert.True(RequireId(first) > 0);
        Assert.True(RequireId(second) > 0);
        Assert.Equal(2, await CountAsync("SELECT COUNT(*) FROM Mails;"));
    }

    [Fact]
    public async Task ExistsAsync_AfterAppend_ReportsTrue()
    {
        await _repo.AppendAsync(Mail("查存在", accountId: "acc-1", messageId: "<exists@example.com>"));

        Assert.True(await _repo.ExistsAsync("acc-1", "<exists@example.com>"));
        Assert.False(await _repo.ExistsAsync("acc-2", "<exists@example.com>"));
    }

    [Fact]
    public async Task AppendAsync_ConcurrentInserts_AllPersisted()
    {
        var mails = Enumerable.Range(0, 10)
            .Select(i => Mail($"并发{i}", messageId: $"<concurrent-{i}@example.com>"))
            .ToArray();

        var ids = await Task.WhenAll(mails.Select(mail => _repo.AppendAsync(mail)));

        Assert.All(ids, id => Assert.True(id.HasValue, "并发追加不应丢信"));
        Assert.Equal(10, ids.Select(id => id.GetValueOrDefault()).Distinct().Count());
        Assert.Equal(10, await CountAsync("SELECT COUNT(*) FROM Mails;"));
    }

    // ─────────────────────────── 列表 / 单封 ───────────────────────────

    [Fact]
    public async Task ListAsync_ReturnsNewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AppendAsync(Mail("旧", sentAt: now.AddHours(-2)));
        await _repo.AppendAsync(Mail("新", sentAt: now));
        await _repo.AppendAsync(Mail("中", sentAt: now.AddHours(-1)));

        var mails = await _repo.ListAsync(accountId: null, limit: 10);

        Assert.Equal(new[] { "新", "中", "旧" }, mails.Select(m => m.Subject));
    }

    [Fact]
    public async Task ListAsync_FiltersByAccount()
    {
        await _repo.AppendAsync(Mail("A 的", accountId: "acc-a"));
        await _repo.AppendAsync(Mail("B 的", accountId: "acc-b"));

        var onlyA = await _repo.ListAsync("acc-a", 10);
        var all = await _repo.ListAsync(null, 10);

        Assert.Equal("A 的", Assert.Single(onlyA).Subject);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task ListAsync_RespectsLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _repo.AppendAsync(Mail($"第{i}封"));
        }

        Assert.Equal(3, (await _repo.ListAsync(null, 3)).Count);
    }

    [Fact]
    public async Task GetAsync_ReturnsMailWithFields()
    {
        var sentAt = new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero);
        var id = RequireId(await _repo.AppendAsync(Mail("带字段", messageId: "<fields@example.com>", sentAt: sentAt)));

        var mail = await _repo.GetAsync(id);

        Assert.NotNull(mail);
        Assert.Equal("带字段", mail.Subject);
        Assert.Equal("acc-1", mail.AccountId);
        Assert.Equal("<fields@example.com>", mail.MessageId);
        Assert.Equal("sender@example.com", mail.FromAddress);
        Assert.Equal(sentAt, mail.SentAt);
        Assert.Null(mail.Summary);
    }

    [Fact]
    public async Task GetAsync_UnknownId_ReturnsNull()
    {
        Assert.Null(await _repo.GetAsync(999_999));
    }

    // ─────────────────────────── 摘要 ───────────────────────────

    [Fact]
    public async Task GetPendingSummaryAsync_ReturnsOldestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.AppendAsync(Mail("新", sentAt: now));
        await _repo.AppendAsync(Mail("旧", sentAt: now.AddHours(-2)));
        await _repo.AppendAsync(Mail("中", sentAt: now.AddHours(-1)));

        var pending = await _repo.GetPendingSummaryAsync(10);

        Assert.Equal(new[] { "旧", "中", "新" }, pending.Select(m => m.Subject));
    }

    [Fact]
    public async Task GetPendingSummaryAsync_SkipsMailsAlreadySummarized()
    {
        var summarized = RequireId(await _repo.AppendAsync(Mail("已有摘要")));
        await _repo.AppendAsync(Mail("还没摘要"));
        await _repo.SaveSummaryAsync(summarized, "摘要内容", needsAction: false, status: "ok", error: null);

        var pending = await _repo.GetPendingSummaryAsync(10);

        Assert.Equal("还没摘要", Assert.Single(pending).Subject);
    }

    [Fact]
    public async Task SaveSummaryAsync_ThenGet_ReturnsSummary()
    {
        var id = RequireId(await _repo.AppendAsync(Mail("要摘要")));

        await _repo.SaveSummaryAsync(id, "三行摘要", needsAction: true, status: "ok", error: null);

        var mail = await _repo.GetAsync(id);
        var summary = Assert.IsType<MailSummaryRow>(mail?.Summary);
        Assert.Equal("三行摘要", summary.Text);
        Assert.True(summary.NeedsAction);
        Assert.True(summary.IsOk);
        Assert.Null(summary.Error);
        Assert.Null(summary.ForwardedAt);
    }

    [Fact]
    public async Task SaveSummaryAsync_Overwrite_ReplacesTextAndKeepsSingleRow()
    {
        var id = RequireId(await _repo.AppendAsync(Mail("先失败后成功")));

        await _repo.SaveSummaryAsync(id, string.Empty, needsAction: false, status: "failed", error: "模型超时");
        await _repo.SaveSummaryAsync(id, "重试后的摘要", needsAction: false, status: "ok", error: null);

        var summary = Assert.IsType<MailSummaryRow>((await _repo.GetAsync(id))?.Summary);
        Assert.Equal("重试后的摘要", summary.Text);
        Assert.Equal("ok", summary.Status);
        Assert.Null(summary.Error);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM MailSummaries;"));

        Assert.Empty(await _repo.GetPendingSummaryAsync(10));
    }

    [Fact]
    public async Task SaveSummaryAsync_UnknownMail_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.SaveSummaryAsync(424_242, "摘要", needsAction: false, status: "ok", error: null));
    }

    // ─────────────────────────── 转发 ───────────────────────────

    [Fact]
    public async Task GetPendingDigestAsync_OnlyReturnsOkAndUnforwarded()
    {
        var okMail = RequireId(await _repo.AppendAsync(Mail("摘要成功")));
        var failedMail = RequireId(await _repo.AppendAsync(Mail("摘要失败")));
        await _repo.SaveSummaryAsync(okMail, "成功摘要", needsAction: false, status: "ok", error: null);
        await _repo.SaveSummaryAsync(failedMail, "失败摘要", needsAction: false, status: "failed", error: "解析失败");

        var pending = await _repo.GetPendingDigestAsync(10);

        // failed 的正文还在，但转发一段"摘要失败"没意义
        Assert.Equal("摘要成功", Assert.Single(pending).Subject);
    }

    [Fact]
    public async Task MarkForwardedAsync_ThenDigest_IsEmpty()
    {
        var id = RequireId(await _repo.AppendAsync(Mail("待转发")));
        await _repo.SaveSummaryAsync(id, "摘要", needsAction: false, status: "ok", error: null);

        await _repo.MarkForwardedAsync(new[] { id });

        Assert.Empty(await _repo.GetPendingDigestAsync(10));
        var summary = Assert.IsType<MailSummaryRow>((await _repo.GetAsync(id))?.Summary);
        Assert.NotNull(summary.ForwardedAt);
    }

    [Fact]
    public async Task MarkForwardedAsync_SecondCall_KeepsFirstForwardTime()
    {
        var id = RequireId(await _repo.AppendAsync(Mail("只转一次")));
        await _repo.SaveSummaryAsync(id, "摘要", needsAction: false, status: "ok", error: null);

        var first = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var later = first.AddHours(3);

        await _repo.MarkForwardedAsync(new[] { id }, first);
        await _repo.MarkForwardedAsync(new[] { id }, later);

        var summary = Assert.IsType<MailSummaryRow>((await _repo.GetAsync(id))?.Summary);
        Assert.Equal(first, summary.ForwardedAt);
    }

    [Fact]
    public async Task MarkForwardedAsync_EmptyList_IsNoOp()
    {
        var id = RequireId(await _repo.AppendAsync(Mail("没标记")));
        await _repo.SaveSummaryAsync(id, "摘要", needsAction: false, status: "ok", error: null);

        await _repo.MarkForwardedAsync(Array.Empty<long>());

        Assert.Single(await _repo.GetPendingDigestAsync(10));
    }

    // ─────────────────────────── 账号 ───────────────────────────

    [Fact]
    public async Task UpsertAccountAsync_NewAccount_Loadable()
    {
        await _repo.UpsertAccountAsync(Account("qq-1", "bot@qq.com"));

        var accounts = await _repo.LoadAccountsAsync();

        var account = Assert.Single(accounts);
        Assert.Equal("qq-1", account.Id);
        Assert.Equal("bot@qq.com", account.Address);
        Assert.Equal("imap.example.com", account.ImapHost);
        Assert.Equal(993, account.ImapPort);
        Assert.True(account.UseSsl);
        Assert.Equal("cipher-v1", account.SecretCipher);
        Assert.True(account.Enabled);
        Assert.Equal(0, account.LastUid);
        Assert.Null(account.LastSyncAt);
    }

    [Fact]
    public async Task UpsertAccountAsync_ExistingAccount_UpdatesConfigOnly()
    {
        await _repo.UpsertAccountAsync(Account("qq-1", "old@qq.com"));
        var lastSync = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);
        await _repo.UpdateSyncStateAsync("qq-1", lastUid: 4321, lastSyncAt: lastSync, lastError: "上次超时");

        // 用户在管理页只改了地址与显示名 —— 同步水位不该被冲掉
        await _repo.UpsertAccountAsync(Account("qq-1", "new@qq.com") with
        {
            DisplayName = "新名字",
            Enabled = false,
        });

        var account = Assert.Single(await _repo.LoadAccountsAsync());
        Assert.Equal("new@qq.com", account.Address);
        Assert.Equal("新名字", account.DisplayName);
        Assert.False(account.Enabled);
        Assert.Equal(4321, account.LastUid);
        Assert.Equal(lastSync, account.LastSyncAt);
        Assert.Equal("上次超时", account.LastError);
    }

    [Fact]
    public async Task LoadAccountsAsync_IncludesDisabledAccounts()
    {
        await _repo.UpsertAccountAsync(Account("qq-1", enabled: true));
        await _repo.UpsertAccountAsync(Account("qq-2", "off@qq.com", enabled: false));

        var accounts = await _repo.LoadAccountsAsync();

        Assert.Equal(2, accounts.Count);
        Assert.False(accounts.Single(a => a.Id == "qq-2").Enabled);
    }

    [Fact]
    public async Task UpdateSyncStateAsync_UpdatesWatermarkAndClearsError()
    {
        await _repo.UpsertAccountAsync(Account("qq-1"));
        var syncAt = new DateTimeOffset(2026, 10, 20, 6, 30, 0, TimeSpan.Zero);

        await _repo.UpdateSyncStateAsync("qq-1", lastUid: 100, lastSyncAt: syncAt, lastError: "连接被拒");
        var failed = Assert.Single(await _repo.LoadAccountsAsync());
        Assert.Equal(100, failed.LastUid);
        Assert.Equal(syncAt, failed.LastSyncAt);
        Assert.Equal("连接被拒", failed.LastError);

        await _repo.UpdateSyncStateAsync("qq-1", lastUid: 200, lastSyncAt: syncAt.AddMinutes(5), lastError: null);
        var ok = Assert.Single(await _repo.LoadAccountsAsync());
        Assert.Equal(200, ok.LastUid);
        Assert.Null(ok.LastError);
        // 配置列不受影响
        Assert.Equal("bot@example.com", ok.Address);
    }

    [Fact]
    public async Task DeleteAccountAsync_RemovesMailsAndSummariesOnlyForThatAccount()
    {
        await _repo.UpsertAccountAsync(Account("acc-a", "a@example.com"));
        await _repo.UpsertAccountAsync(Account("acc-b", "b@example.com"));

        var mailA = RequireId(await _repo.AppendAsync(Mail("A 的邮件", accountId: "acc-a")));
        var mailB = RequireId(await _repo.AppendAsync(Mail("B 的邮件", accountId: "acc-b")));
        await _repo.SaveSummaryAsync(mailA, "A 摘要", needsAction: false, status: "ok", error: null);
        await _repo.SaveSummaryAsync(mailB, "B 摘要", needsAction: false, status: "ok", error: null);

        await _repo.DeleteAccountAsync("acc-a");

        var account = Assert.Single(await _repo.LoadAccountsAsync());
        Assert.Equal("acc-b", account.Id);
        Assert.Empty(await _repo.ListAsync("acc-a", 10));
        Assert.Single(await _repo.ListAsync("acc-b", 10));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM Mails;"));
        // 摘要必须跟着走，不能留孤儿行
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM MailSummaries;"));
        Assert.Equal(0, await CountAsync(
            "SELECT COUNT(*) FROM MailSummaries s LEFT JOIN Mails m ON m.Id = s.MailId WHERE m.Id IS NULL;"));
    }
}
