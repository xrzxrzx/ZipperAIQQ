using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Messages;

/// <summary>
/// <see cref="IMessageStore"/> 的 SQLite 实现。
/// </summary>
/// <remarks>
/// <para>
/// **不自建连接**：读写一律走 <see cref="ISqliteDatabase"/>（FR-16.1）。
/// 这样多个 agent 实例并发写库时，并发点只有一个（统一入口里的信号量），
/// 而不是散落在每个仓储里。
/// </para>
/// <para>
/// 全部走**参数化**，不拼 SQL 字符串 —— 消息内容来自任意群成员，属于不可信输入。
/// </para>
/// </remarks>
public sealed class SqliteMessageStore : IMessageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISqliteDatabase _db;

    public SqliteMessageStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<long> AppendAsync(IncomingMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return AppendCoreAsync(message, direction: 0, ct);
    }

    public Task<long> AppendOutgoingAsync(
        OutgoingMessage message, string senderUserId, DateTimeOffset sentAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderUserId);

        // 出站消息复用同一张表：把 Sender 记成机器人自己，Direction=1 区分方向
        var asInbound = new IncomingMessage
        {
            Chat = message.Chat,
            Sender = new SenderInfo { UserId = senderUserId, Nickname = "AI" },
            Segments = message.Segments,
            OccurredAt = sentAt,
            ReplyToMessageId = message.ReplyToMessageId,
        };

        return AppendCoreAsync(asInbound, direction: 1, ct);
    }

    public Task<long> AppendCommandAsync(
        OutgoingMessage message, string senderUserId, DateTimeOffset sentAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderUserId);

        // Direction=2：命令响应。和 AI 发言分开（FR-9.4），恢复历史时不会当成"它说过的话"
        var asInbound = new IncomingMessage
        {
            Chat = message.Chat,
            Sender = new SenderInfo { UserId = "system", Nickname = "系统" },
            Segments = message.Segments,
            OccurredAt = sentAt,
            ReplyToMessageId = message.ReplyToMessageId,
        };

        return AppendCoreAsync(asInbound, direction: 2, ct);
    }

    private Task<long> AppendCoreAsync(IncomingMessage message, int direction, CancellationToken ct)
    {
        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = InsertSql;

            cmd.Parameters.AddWithValue("@platform", message.Chat.Platform);
            cmd.Parameters.AddWithValue("@sessionId", message.Chat.SessionId);
            cmd.Parameters.AddWithValue("@chatType", (int)message.Chat.Type);
            cmd.Parameters.AddWithValue("@chatId", message.Chat.Id);
            cmd.Parameters.AddWithValue("@messageId", (object?)message.EventId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@senderUserId", message.Sender.UserId);
            cmd.Parameters.AddWithValue("@senderGroupCard", (object?)message.Sender.GroupCard ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@senderNickname", (object?)message.Sender.Nickname ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@senderIsFriend", message.Sender.IsFriend ? 1 : 0);
            cmd.Parameters.AddWithValue("@plainText", message.PlainText);
            cmd.Parameters.AddWithValue("@segmentsJson", JsonSerializer.Serialize(message.Segments, JsonOptions));
            cmd.Parameters.AddWithValue("@sentAtMs", message.OccurredAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@receivedAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@replyToMessageId", (object?)message.ReplyToMessageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@direction", direction);

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }, ct);
    }

    public Task<IReadOnlyList<StoredMessage>> GetRecentAsync(
        string sessionId, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<StoredMessage>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            // 先按时间倒序取最近 N 条，再反转成正序
            cmd.CommandText = SelectRecentSql;
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.Parameters.AddWithValue("@limit", limit);

            var buffer = new List<StoredMessage>(capacity: Math.Min(limit, 256));

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                buffer.Add(ReadRow(reader));
            }

            buffer.Reverse();
            return buffer;
        }, ct);
    }

    public Task<IReadOnlyList<StoredMessage>> GetByIdsAsync(
        string sessionId, IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<StoredMessage>>([]);
        }

        return _db.ReadAsync<IReadOnlyList<StoredMessage>>(async (conn, token) =>
        {
            // IN 子句必须逐个占位符 —— 拼字符串会把外部输入当 SQL 执行
            var placeholders = string.Join(", ", Enumerable.Range(0, ids.Count).Select(i => $"@p{i}"));

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {SelectColumnsSql}
                FROM Messages
                WHERE SessionId = @sessionId AND Id IN ({placeholders})
                ORDER BY SentAtMs ASC, Id ASC;
                """;
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            for (var i = 0; i < ids.Count; i++)
            {
                cmd.Parameters.AddWithValue($"@p{i}", ids[i]);
            }

            var list = new List<StoredMessage>(ids.Count);
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(ReadRow(reader));
            }

            return (IReadOnlyList<StoredMessage>)list;
        }, ct);
    }

    public Task<long> CountAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Messages WHERE SessionId = @sessionId;";
            cmd.Parameters.AddWithValue("@sessionId", sessionId);

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }, ct);
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<SessionSummary>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT SessionId, COUNT(*), MAX(SentAtMs)
                FROM Messages
                GROUP BY SessionId
                ORDER BY MAX(SentAtMs) DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = new List<SessionSummary>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new SessionSummary(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
            }

            return list;
        }, ct);
    }

    public Task<int> PurgeAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Messages WHERE SessionId = @sessionId;";
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    private static StoredMessage ReadRow(SqliteDataReader reader)
    {
        var segmentsJson = reader.GetString(11);
        var segments = JsonSerializer.Deserialize<List<MessageSegment>>(segmentsJson, JsonOptions) ?? [];

        var chat = new ChatRef
        {
            Platform = reader.GetString(1),
            Type = (ChatType)reader.GetInt32(2),
            Id = reader.GetString(3),
        };

        var sender = new SenderInfo
        {
            UserId = reader.GetString(5),
            GroupCard = reader.IsDBNull(6) ? null : reader.GetString(6),
            Nickname = reader.IsDBNull(7) ? null : reader.GetString(7),
            IsFriend = reader.GetInt32(8) != 0,
        };

        var message = new IncomingMessage
        {
            Chat = chat,
            Sender = sender,
            Segments = segments,
            OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
            EventId = reader.IsDBNull(4) ? null : reader.GetString(4),
            ReplyToMessageId = reader.IsDBNull(12) ? null : reader.GetString(12),
        };

        return new StoredMessage(
            Id: reader.GetInt64(0),
            Message: message,
            ReceivedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
            IsOutgoing: reader.GetInt32(13) == 1,
            IsCommand: reader.GetInt32(13) == 2);
    }

    private const string InsertSql = """
        INSERT INTO Messages (
            Platform, SessionId, ChatType, ChatId, MessageId,
            SenderUserId, SenderGroupCard, SenderNickname, SenderIsFriend,
            PlainText, SegmentsJson, SentAtMs, ReceivedAtMs, ReplyToMessageId, Direction
        ) VALUES (
            @platform, @sessionId, @chatType, @chatId, @messageId,
            @senderUserId, @senderGroupCard, @senderNickname, @senderIsFriend,
            @plainText, @segmentsJson, @sentAtMs, @receivedAtMs, @replyToMessageId, @direction
        )
        RETURNING Id;
        """;

    private const string SelectColumnsSql = """
        Id, Platform, ChatType, ChatId, MessageId,
        SenderUserId, SenderGroupCard, SenderNickname, SenderIsFriend,
        ReceivedAtMs, SentAtMs, SegmentsJson, ReplyToMessageId, Direction
        """;

    /// <summary>关键词搜索（LIKE）；参数化，绝不拼字符串。</summary>
    public Task<IReadOnlyList<StoredMessage>> SearchAsync(
        string sessionId, string keyword, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<StoredMessage>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectSearchSql;
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.Parameters.AddWithValue("@pattern", "%" + keyword + "%");
            cmd.Parameters.AddWithValue("@limit", limit);

            var buffer = new List<StoredMessage>(capacity: Math.Min(limit, 128));

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                buffer.Add(ReadRow(reader));
            }

            buffer.Reverse();
            return buffer;
        }, ct);
    }

    /// <summary>跨会话关键词搜索；参数化，绝不拼字符串。</summary>
    public Task<IReadOnlyList<StoredMessage>> SearchAcrossSessionsAsync(
        string keyword, int limit, string? excludeSessionId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<StoredMessage>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = SelectCrossSessionSearchSql;
            cmd.Parameters.AddWithValue("@pattern", "%" + keyword + "%");
            cmd.Parameters.AddWithValue("@exclude", excludeSessionId ?? string.Empty);
            cmd.Parameters.AddWithValue("@limit", limit);

            var buffer = new List<StoredMessage>(capacity: Math.Min(limit, 128));

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                buffer.Add(ReadRow(reader));
            }

            buffer.Reverse();
            return buffer;
        }, ct);
    }

    private const string SelectCrossSessionSearchSql = """
        SELECT Id, Platform, ChatType, ChatId, MessageId,
               SenderUserId, SenderGroupCard, SenderNickname, SenderIsFriend,
               ReceivedAtMs, SentAtMs, SegmentsJson, ReplyToMessageId, Direction
        FROM Messages
        WHERE PlainText LIKE @pattern
          AND (@exclude = '' OR SessionId <> @exclude)
        ORDER BY SentAtMs DESC, Id DESC
        LIMIT @limit;
        """;

    private const string SelectSearchSql = """
        SELECT Id, Platform, ChatType, ChatId, MessageId,
               SenderUserId, SenderGroupCard, SenderNickname, SenderIsFriend,
               ReceivedAtMs, SentAtMs, SegmentsJson, ReplyToMessageId, Direction
        FROM Messages
        WHERE SessionId = @sessionId AND PlainText LIKE @pattern
        ORDER BY SentAtMs DESC, Id DESC
        LIMIT @limit;
        """;

    private const string SelectRecentSql = """
        SELECT Id, Platform, ChatType, ChatId, MessageId,
               SenderUserId, SenderGroupCard, SenderNickname, SenderIsFriend,
               ReceivedAtMs, SentAtMs, SegmentsJson, ReplyToMessageId, Direction
        FROM Messages
        WHERE SessionId = @sessionId
        ORDER BY SentAtMs DESC, Id DESC
        LIMIT @limit;
        """;
}
