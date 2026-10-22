using Microsoft.Data.Sqlite;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Schedules;

/// <summary>
/// <see cref="IScheduleStore"/> 的 SQLite 实现。
/// </summary>
/// <remarks>
/// 同样走 <see cref="ISqliteDatabase"/> 统一入口；全部参数化，不拼 SQL。
/// </remarks>
public sealed class SqliteScheduleStore : IScheduleStore
{
    private readonly ISqliteDatabase _db;

    public SqliteScheduleStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<long> CreateAsync(ScheduledTask task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ScheduleTasks (
                    SessionId, Platform, ChatType, ChatId, Content, MentionUserId,
                    RepeatKind, IntervalMinutes, NextRunAtMs, CreatedAtMs, CreatedBy, Status
                ) VALUES (
                    @sessionId, @platform, @chatType, @chatId, @content, @mentionUserId,
                    @repeatKind, @intervalMinutes, @nextRunAtMs, @createdAtMs, @createdBy, @status
                )
                RETURNING Id;
                """;

            cmd.Parameters.AddWithValue("@sessionId", task.Chat.SessionId);
            cmd.Parameters.AddWithValue("@platform", task.Chat.Platform);
            cmd.Parameters.AddWithValue("@chatType", (int)task.Chat.Type);
            cmd.Parameters.AddWithValue("@chatId", task.Chat.Id);
            cmd.Parameters.AddWithValue("@content", task.Content);
            cmd.Parameters.AddWithValue("@mentionUserId", (object?)task.MentionUserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@repeatKind", (int)task.Repeat);
            cmd.Parameters.AddWithValue("@intervalMinutes", task.IntervalMinutes);
            cmd.Parameters.AddWithValue("@nextRunAtMs", task.NextRunAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@createdAtMs", task.CreatedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@createdBy", task.CreatedBy);
            cmd.Parameters.AddWithValue("@status", (int)task.Status);

            var id = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(id);
        }, ct);
    }

    public Task<IReadOnlyList<ScheduledTask>> GetDueAsync(
        DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<ScheduledTask>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Platform, ChatType, ChatId, Content, MentionUserId,
                       RepeatKind, IntervalMinutes, NextRunAtMs, CreatedAtMs, CreatedBy, Status
                FROM ScheduleTasks
                WHERE Status = 0 AND NextRunAtMs <= @nowMs
                ORDER BY NextRunAtMs
                LIMIT @limit;
                """;

            cmd.Parameters.AddWithValue("@nowMs", now.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = new List<ScheduledTask>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(ReadRow(reader));
            }

            return list;
        }, ct);
    }

    public Task<IReadOnlyList<ScheduledTask>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _db.ReadAsync<IReadOnlyList<ScheduledTask>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Platform, ChatType, ChatId, Content, MentionUserId,
                       RepeatKind, IntervalMinutes, NextRunAtMs, CreatedAtMs, CreatedBy, Status
                FROM ScheduleTasks
                WHERE SessionId = @sessionId
                ORDER BY NextRunAtMs;
                """;

            cmd.Parameters.AddWithValue("@sessionId", sessionId);

            var list = new List<ScheduledTask>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(ReadRow(reader));
            }

            return list;
        }, ct);
    }

    public async Task<bool> CancelAsync(long id, string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var affected = await _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE ScheduleTasks
                SET Status = 2
                WHERE Id = @id AND SessionId = @sessionId AND Status = 0;
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@sessionId", sessionId);
            return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return affected > 0;
    }

    public Task MarkDoneAsync(long id, CancellationToken ct = default)
    {
        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE ScheduleTasks SET Status = 1 WHERE Id = @id AND Status = 0;";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task RescheduleAsync(long id, DateTimeOffset nextRunAt, CancellationToken ct = default)
    {
        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE ScheduleTasks SET NextRunAtMs = @next WHERE Id = @id AND Status = 0;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@next", nextRunAt.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        return _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ScheduleTasks WHERE Status = 0;";
            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt32(result);
        }, ct);
    }

    private static ScheduledTask ReadRow(SqliteDataReader reader)
    {
        var chat = new ChatRef
        {
            Platform = reader.GetString(1),
            Type = (ChatType)reader.GetInt32(2),
            Id = reader.GetString(3),
        };

        return new ScheduledTask(
            Id: reader.GetInt64(0),
            Chat: chat,
            Content: reader.GetString(4),
            MentionUserId: reader.IsDBNull(5) ? null : reader.GetString(5),
            Repeat: (ScheduleRepeat)reader.GetInt32(6),
            IntervalMinutes: reader.GetInt32(7),
            NextRunAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
            CreatedBy: reader.GetString(10),
            Status: (ScheduleStatus)reader.GetInt32(11));
    }
}
