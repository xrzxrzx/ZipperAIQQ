using Microsoft.Data.Sqlite;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Mails;

/// <summary>
/// <see cref="IMailRepository"/> 的 SQLite 实现。
/// </summary>
/// <remarks>
/// <para>
/// 读写一律走 <see cref="ISqliteDatabase"/> 统一入口（FR-16.1）：邮件同步循环与
/// 摘要 / 汇总服务会并发写同一个库，并发点必须只有一个。
/// </para>
/// <para>
/// 全部**参数化**：主题、正文、发件人地址都来自外部邮件，属于不可信输入。
/// </para>
/// <para>
/// ⚠️ 需要多条语句配合的写操作（追加去重、删账号级联、批量标记），一律在**同一个
/// <see cref="ISqliteDatabase.WriteAsync{T}"/> 委托**里用**同一事务**做完 ——
/// 统一入口的信号量不可重入，嵌套 <c>WriteAsync</c> 会死锁。
/// </para>
/// <para>
/// 时间列一律存 **UTC Unix 毫秒**（<c>ToUnixTimeMilliseconds</c>），不存本地时间字符串：
/// 时区混用属于那种「平时正常、偶尔错 8 小时」的故障（FR-15.6）。
/// </para>
/// </remarks>
public sealed class SqliteMailRepository : IMailRepository
{
    /// <summary>SQLite 约束冲突（UNIQUE / PRIMARY KEY / NOT NULL）的错误码。</summary>
    private const int ConstraintViolationErrorCode = 19;

    /// <summary>
    /// 邮件行 + 摘要行的列清单。两个「带摘要」的查询共用它，
    /// 免得到处手写列顺序、改一处忘一处。
    /// </summary>
    private const string MailSelectPrefix = """
        SELECT m.Id, m.AccountId, m.MessageId, m.Subject, m.FromAddress, m.FromName,
               m.BodyText, m.HasAttachments, m.SentAtMs, m.ReceivedAtMs,
               s.Summary, s.NeedsAction, s.Status, s.Error, s.UpdatedAtMs, s.ForwardedAtMs
        FROM Mails m
        LEFT JOIN MailSummaries s ON s.MailId = m.Id
        """;

    private readonly ISqliteDatabase _db;

    public SqliteMailRepository(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    // ─────────────────────────── 邮件 ───────────────────────────

    public Task<long?> AppendAsync(IncomingMail mail, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentException.ThrowIfNullOrWhiteSpace(mail.AccountId);

        // 空 / 空白 Message-Id 统一按「没有 ID」处理：唯一索引只约束非 NULL，
        // 若把空串直接存进去，多封「没 ID」的邮件会互相撞成一条。
        var messageId = string.IsNullOrWhiteSpace(mail.EventId) ? null : mail.EventId;

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var tx = (SqliteTransaction)await conn
                .BeginTransactionAsync(token).ConfigureAwait(false);

            // 写前预检与插入必须同事务：预检放事务外的话，两个连接可以同时
            // 「看到没有 → 都去插」，唯一索引会替我们兜底，但那要当异常处理。
            if (messageId is not null
                && await ExistsCoreAsync(conn, tx, mail.AccountId, messageId, token).ConfigureAwait(false))
            {
                // 不提交，dispose 时回滚（本来也没写任何东西）
                return (long?)null;
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO Mails (
                    AccountId, MessageId, Subject, FromAddress, FromName,
                    BodyText, HasAttachments, SentAtMs, ReceivedAtMs
                ) VALUES (
                    @accountId, @messageId, @subject, @fromAddress, @fromName,
                    @bodyText, @hasAttachments, @sentAtMs, @receivedAtMs
                )
                RETURNING Id;
                """;

            cmd.Parameters.AddWithValue("@accountId", mail.AccountId);
            cmd.Parameters.AddWithValue("@messageId", (object?)messageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@subject", mail.Subject);
            cmd.Parameters.AddWithValue("@fromAddress", mail.FromAddress);
            cmd.Parameters.AddWithValue("@fromName", (object?)mail.FromName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@bodyText", mail.BodyText);
            cmd.Parameters.AddWithValue("@hasAttachments", mail.HasAttachments ? 1 : 0);
            cmd.Parameters.AddWithValue("@sentAtMs", mail.OccurredAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@receivedAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            try
            {
                var id = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                await tx.CommitAsync(token).ConfigureAwait(false);
                return (long?)Convert.ToInt64(id);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == ConstraintViolationErrorCode)
            {
                // 预检到插入之间被别的进程抢插了同一条（本实现只保证进程内串行）——
                // 唯一索引赢了，语义同样是「这次没新增」，不往上抛。
                await tx.RollbackAsync(token).ConfigureAwait(false);
                return (long?)null;
            }
        }, ct);
    }

    public Task<bool> ExistsAsync(string accountId, string messageId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        return _db.ReadAsync(
            (conn, token) => ExistsCoreAsync(conn, transaction: null, accountId, messageId, token),
            ct);
    }

    public Task<IReadOnlyList<StoredMail>> ListAsync(
        string? accountId, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<StoredMail>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = MailSelectPrefix + """

                WHERE (@accountId IS NULL OR m.AccountId = @accountId)
                ORDER BY m.SentAtMs DESC, m.Id DESC
                LIMIT @limit;
                """;

            cmd.Parameters.AddWithValue("@accountId", (object?)accountId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@limit", limit);

            return await ReadMailsAsync(cmd, token).ConfigureAwait(false);
        }, ct);
    }

    public Task<StoredMail?> GetAsync(long id, CancellationToken ct = default)
    {
        return _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = MailSelectPrefix + """

                WHERE m.Id = @id
                LIMIT 1;
                """;

            cmd.Parameters.AddWithValue("@id", id);

            var mails = await ReadMailsAsync(cmd, token).ConfigureAwait(false);
            return mails.Count == 0 ? null : mails[0];
        }, ct);
    }

    public Task<IReadOnlyList<StoredMail>> GetPendingSummaryAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<StoredMail>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            // LEFT JOIN + 摘要行缺席 = 还没摘要过。旧的在先：补做顺序跟着收信顺序走。
            cmd.CommandText = MailSelectPrefix + """

                WHERE s.MailId IS NULL
                ORDER BY m.SentAtMs, m.Id
                LIMIT @limit;
                """;

            cmd.Parameters.AddWithValue("@limit", limit);

            return await ReadMailsAsync(cmd, token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<StoredMail>> GetPendingDigestAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<StoredMail>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            // 只要摘要成功的（failed 的正文还在，但转发一段"摘要失败"没意义），
            // 且还没转发过 —— 否则 interval / daily 模式会反复转同一批。
            cmd.CommandText = MailSelectPrefix + """

                WHERE s.Status = 'ok' AND s.ForwardedAtMs IS NULL
                ORDER BY m.SentAtMs, m.Id
                LIMIT @limit;
                """;

            cmd.Parameters.AddWithValue("@limit", limit);

            return await ReadMailsAsync(cmd, token).ConfigureAwait(false);
        }, ct);
    }

    public Task SaveSummaryAsync(
        long mailId,
        string summary,
        bool needsAction,
        string status,
        string? error,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var tx = (SqliteTransaction)await conn
                .BeginTransactionAsync(token).ConfigureAwait(false);

            if (!await MailExistsAsync(conn, tx, mailId, token).ConfigureAwait(false))
            {
                // 契约要求显式抛：摘要服务拿到"写了但没落库"是最难查的一类 bug
                throw new InvalidOperationException($"邮件 {mailId} 不存在，无法写摘要");
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // 覆盖时**不动 ForwardedAtMs**：重新摘要不等于要再转发一遍。
            cmd.CommandText = """
                INSERT INTO MailSummaries (
                    MailId, Summary, NeedsAction, Status, Error, UpdatedAtMs, ForwardedAtMs
                ) VALUES (
                    @mailId, @summary, @needsAction, @status, @error, @updatedAtMs, NULL
                )
                ON CONFLICT(MailId) DO UPDATE SET
                    Summary     = excluded.Summary,
                    NeedsAction = excluded.NeedsAction,
                    Status      = excluded.Status,
                    Error       = excluded.Error,
                    UpdatedAtMs = excluded.UpdatedAtMs;
                """;

            cmd.Parameters.AddWithValue("@mailId", mailId);
            cmd.Parameters.AddWithValue("@summary", summary);
            cmd.Parameters.AddWithValue("@needsAction", needsAction ? 1 : 0);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@updatedAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await tx.CommitAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task MarkForwardedAsync(
        IReadOnlyList<long> mailIds,
        DateTimeOffset? forwardedAt = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mailIds);

        if (mailIds.Count == 0)
        {
            // 空批在汇总循环里是常态（这一轮没新邮件），不该开连接
            return Task.CompletedTask;
        }

        var forwardedAtMs = (forwardedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var tx = (SqliteTransaction)await conn
                .BeginTransactionAsync(token).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // IS NULL 条件不能省：并发两轮汇总时，先到的那轮写时间，后到的这轮不覆盖，
            // 「什么时候第一次转发出去的」才是可信的。
            cmd.CommandText = """
                UPDATE MailSummaries
                SET ForwardedAtMs = @forwardedAtMs
                WHERE MailId = @mailId AND ForwardedAtMs IS NULL;
                """;

            var idParameter = cmd.Parameters.Add("@mailId", SqliteType.Integer);
            cmd.Parameters.AddWithValue("@forwardedAtMs", forwardedAtMs);

            foreach (var mailId in mailIds)
            {
                idParameter.Value = mailId;
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await tx.CommitAsync(token).ConfigureAwait(false);
        }, ct);
    }

    // ─────────────────────────── 账号 ───────────────────────────

    public Task<IReadOnlyList<MailAccount>> LoadAccountsAsync(CancellationToken ct = default)
    {
        return _db.ReadAsync<IReadOnlyList<MailAccount>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Address, DisplayName, ImapHost, ImapPort, UseSsl, Username,
                       SecretCipher, Enabled, LastUid, LastSyncAtMs, LastError, CreatedAtMs
                FROM MailAccounts
                ORDER BY CreatedAtMs, Id;
                """;

            var accounts = new List<MailAccount>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                accounts.Add(ReadAccount(reader));
            }

            return accounts;
        }, ct);
    }

    public Task UpsertAccountAsync(MailAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Id);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            // 冲突时**只更新配置列**：同步水位 / 上次同步时间 / 上次错误是同步循环的地盘，
            // 用户在管理页点一次"保存"不该把它们冲回默认值。
            cmd.CommandText = """
                INSERT INTO MailAccounts (
                    Id, Address, DisplayName, ImapHost, ImapPort, UseSsl, Username,
                    SecretCipher, Enabled, LastUid, LastSyncAtMs, LastError, CreatedAtMs
                ) VALUES (
                    @id, @address, @displayName, @imapHost, @imapPort, @useSsl, @username,
                    @secretCipher, @enabled, @lastUid, @lastSyncAtMs, @lastError, @createdAtMs
                )
                ON CONFLICT(Id) DO UPDATE SET
                    Address      = excluded.Address,
                    DisplayName  = excluded.DisplayName,
                    ImapHost     = excluded.ImapHost,
                    ImapPort     = excluded.ImapPort,
                    UseSsl       = excluded.UseSsl,
                    Username     = excluded.Username,
                    SecretCipher = excluded.SecretCipher,
                    Enabled      = excluded.Enabled;
                """;

            cmd.Parameters.AddWithValue("@id", account.Id);
            cmd.Parameters.AddWithValue("@address", account.Address);
            cmd.Parameters.AddWithValue("@displayName", (object?)account.DisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@imapHost", account.ImapHost);
            cmd.Parameters.AddWithValue("@imapPort", account.ImapPort);
            cmd.Parameters.AddWithValue("@useSsl", account.UseSsl ? 1 : 0);
            cmd.Parameters.AddWithValue("@username", account.Username);
            cmd.Parameters.AddWithValue("@secretCipher", account.SecretCipher);
            cmd.Parameters.AddWithValue("@enabled", account.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@lastUid", account.LastUid);
            cmd.Parameters.AddWithValue(
                "@lastSyncAtMs",
                account.LastSyncAt is null ? DBNull.Value : account.LastSyncAt.Value.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@lastError", (object?)account.LastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAtMs", account.CreatedAt.ToUnixTimeMilliseconds());

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task DeleteAccountAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var tx = (SqliteTransaction)await conn
                .BeginTransactionAsync(token).ConfigureAwait(false);

            // 顺序必须是「摘要 → 邮件 → 账号」：反过来的话，删完账号就找不到它的邮件了，
            // 那些行会变成谁也访问不到、谁也删不掉的孤儿（现在没有外键兜底）。
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM MailSummaries
                WHERE MailId IN (SELECT Id FROM Mails WHERE AccountId = @id);

                DELETE FROM Mails WHERE AccountId = @id;

                DELETE FROM MailAccounts WHERE Id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", id);

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await tx.CommitAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task UpdateSyncStateAsync(
        string accountId,
        long lastUid,
        DateTimeOffset? lastSyncAt,
        string? lastError,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            // 只碰这三列：高频写，整行覆盖会把用户刚改的配置冲掉
            cmd.CommandText = """
                UPDATE MailAccounts
                SET LastUid      = @lastUid,
                    LastSyncAtMs = @lastSyncAtMs,
                    LastError    = @lastError
                WHERE Id = @id;
                """;

            cmd.Parameters.AddWithValue("@id", accountId);
            cmd.Parameters.AddWithValue("@lastUid", lastUid);
            cmd.Parameters.AddWithValue(
                "@lastSyncAtMs",
                lastSyncAt is null ? DBNull.Value : lastSyncAt.Value.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@lastError", (object?)lastError ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    // ─────────────────────────── 私有辅助 ───────────────────────────

    private static async Task<bool> ExistsCoreAsync(
        SqliteConnection conn,
        SqliteTransaction? transaction,
        string accountId,
        string messageId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT 1 FROM Mails WHERE AccountId = @accountId AND MessageId = @messageId LIMIT 1;";
        cmd.Parameters.AddWithValue("@accountId", accountId);
        cmd.Parameters.AddWithValue("@messageId", messageId);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    private static async Task<bool> MailExistsAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        long mailId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT 1 FROM Mails WHERE Id = @id LIMIT 1;";
        cmd.Parameters.AddWithValue("@id", mailId);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    private static async Task<IReadOnlyList<StoredMail>> ReadMailsAsync(
        SqliteCommand cmd, CancellationToken ct)
    {
        var mails = new List<StoredMail>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            mails.Add(ReadMail(reader));
        }

        return mails;
    }

    private static StoredMail ReadMail(SqliteDataReader reader)
    {
        var summary = reader.IsDBNull(10)
            ? null
            : new MailSummaryRow(
                Text: reader.GetString(10),
                NeedsAction: reader.GetInt32(11) != 0,
                Status: reader.GetString(12),
                Error: reader.IsDBNull(13) ? null : reader.GetString(13),
                UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
                ForwardedAt: reader.IsDBNull(15)
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(15)));

        return new StoredMail(
            Id: reader.GetInt64(0),
            AccountId: reader.GetString(1),
            MessageId: reader.IsDBNull(2) ? null : reader.GetString(2),
            Subject: reader.GetString(3),
            FromAddress: reader.GetString(4),
            FromName: reader.IsDBNull(5) ? null : reader.GetString(5),
            BodyText: reader.GetString(6),
            HasAttachments: reader.GetInt32(7) != 0,
            SentAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            ReceivedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
            Summary: summary);
    }

    private static MailAccount ReadAccount(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Address = reader.GetString(1),
        DisplayName = reader.IsDBNull(2) ? null : reader.GetString(2),
        ImapHost = reader.GetString(3),
        ImapPort = reader.GetInt32(4),
        UseSsl = reader.GetInt32(5) != 0,
        Username = reader.GetString(6),
        SecretCipher = reader.GetString(7),
        Enabled = reader.GetInt32(8) != 0,
        LastUid = reader.GetInt64(9),
        LastSyncAt = reader.IsDBNull(10)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
        LastError = reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
    };
}
