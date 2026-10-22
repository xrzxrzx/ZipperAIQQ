using Microsoft.Data.Sqlite;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Notices;

/// <summary>一条已入库的通知。</summary>
public sealed record StoredNotice(long Id, IncomingNotice Notice, DateTimeOffset StoredAt);

/// <summary>
/// 通知落库（FR-18.1）：通知与消息**分开存**，避免污染检索。
/// </summary>
/// <remarks>
/// <c>Actor</c> / <c>Target</c> 分开记（FR-18.4）：戳一戳是「A 戳 B」，
/// 只记"发生了一次戳一戳"的话，AI 分不清该对谁说话。
/// </remarks>
public interface INoticeStore
{
    Task<long> AppendAsync(IncomingNotice notice, CancellationToken ct = default);

    Task<IReadOnlyList<StoredNotice>> ListRecentAsync(string sessionId, int limit, CancellationToken ct = default);

    Task<int> CountAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>SQLite 实现。</summary>
public sealed class SqliteNoticeStore : INoticeStore
{
    private readonly ISqliteDatabase _db;

    public SqliteNoticeStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<long> AppendAsync(IncomingNotice notice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Notices (
                    SessionId, Platform, ChatType, ChatId, NoticeType, RawType,
                    ActorUserId, ActorName, TargetUserId, TargetName, RelatedMessageId, OccurredAtMs
                ) VALUES (
                    @sessionId, @platform, @chatType, @chatId, @noticeType, @rawType,
                    @actorUserId, @actorName, @targetUserId, @targetName, @relatedMessageId, @occurredAtMs
                )
                RETURNING Id;
                """;

            cmd.Parameters.AddWithValue("@sessionId", notice.Chat.SessionId);
            cmd.Parameters.AddWithValue("@platform", notice.Chat.Platform);
            cmd.Parameters.AddWithValue("@chatType", (int)notice.Chat.Type);
            cmd.Parameters.AddWithValue("@chatId", notice.Chat.Id);
            cmd.Parameters.AddWithValue("@noticeType", (int)notice.Type);
            cmd.Parameters.AddWithValue("@rawType", notice.RawType);
            cmd.Parameters.AddWithValue("@actorUserId", (object?)notice.Actor?.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@actorName", (object?)notice.Actor?.DisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@targetUserId", (object?)notice.Target?.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@targetName", (object?)notice.Target?.DisplayName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@relatedMessageId", (object?)notice.RelatedMessageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@occurredAtMs", notice.OccurredAt.ToUnixTimeMilliseconds());

            var id = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(id);
        }, ct);
    }

    public Task<IReadOnlyList<StoredNotice>> ListRecentAsync(
        string sessionId, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<StoredNotice>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Platform, ChatType, ChatId, NoticeType, RawType,
                       ActorUserId, ActorName, TargetUserId, TargetName, RelatedMessageId, OccurredAtMs
                FROM Notices
                WHERE SessionId = @sessionId
                ORDER BY OccurredAtMs DESC, Id DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = new List<StoredNotice>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var chat = new ChatRef
                {
                    Platform = reader.GetString(1),
                    Type = (ChatType)reader.GetInt32(2),
                    Id = reader.GetString(3),
                };

                var notice = new IncomingNotice
                {
                    Chat = chat,
                    Type = (NoticeType)reader.GetInt32(4),
                    RawType = reader.GetString(5),
                    Actor = reader.IsDBNull(6) ? null : new SenderInfo
                    {
                        UserId = reader.GetString(6),
                        Nickname = reader.IsDBNull(7) ? null : reader.GetString(7),
                    },
                    Target = reader.IsDBNull(8) ? null : new SenderInfo
                    {
                        UserId = reader.GetString(8),
                        Nickname = reader.IsDBNull(9) ? null : reader.GetString(9),
                    },
                    RelatedMessageId = reader.IsDBNull(10) ? null : reader.GetString(10),
                    OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
                };

                list.Add(new StoredNotice(reader.GetInt64(0), notice, notice.OccurredAt));
            }

            return list;
        }, ct);
    }

    public Task<int> CountAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Notices WHERE SessionId = @sessionId;";
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt32(result);
        }, ct);
    }
}
