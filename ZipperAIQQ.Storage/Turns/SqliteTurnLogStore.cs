using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Turns;

/// <summary><see cref="ITurnLogStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteTurnLogStore : ITurnLogStore
{
    private readonly ISqliteDatabase _db;

    public SqliteTurnLogStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task AppendAsync(TurnLog log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(log);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO TurnLogs (SessionId, StartedAtMs, EndedAtMs, EndReason, SteerCount, FollowUpCount)
                VALUES (@sessionId, @startedAtMs, @endedAtMs, @endReason, @steerCount, @followUpCount);
                """;
            cmd.Parameters.AddWithValue("@sessionId", log.SessionId);
            cmd.Parameters.AddWithValue("@startedAtMs", log.StartedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@endedAtMs", log.EndedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@endReason", log.EndReason);
            cmd.Parameters.AddWithValue("@steerCount", log.SteerCount);
            cmd.Parameters.AddWithValue("@followUpCount", log.FollowUpCount);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<TurnLog>> ListRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<TurnLog>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, SessionId, StartedAtMs, EndedAtMs, EndReason, SteerCount, FollowUpCount
                FROM TurnLogs
                ORDER BY Id DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = new List<TurnLog>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new TurnLog(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6)));
            }

            return list;
        }, ct);
    }
}
