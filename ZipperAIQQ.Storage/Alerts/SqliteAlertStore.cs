using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Alerts;

/// <summary><see cref="IAlertStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteAlertStore : IAlertStore
{
    private readonly ISqliteDatabase _db;

    public SqliteAlertStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task AppendAsync(AlertRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Alerts (Kind, Platform, Target, Ok, Error, CreatedAtMs)
                VALUES (@kind, @platform, @target, @ok, @error, @createdAtMs);
                """;
            cmd.Parameters.AddWithValue("@kind", record.Kind);
            cmd.Parameters.AddWithValue("@platform", (object?)record.Platform ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@target", (object?)record.Target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ok", record.Ok ? 1 : 0);
            cmd.Parameters.AddWithValue("@error", (object?)record.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAtMs", record.CreatedAt.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<bool> ExistsRecentAsync(
        string kind,
        string? platform,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        return _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM Alerts
                WHERE Kind = @kind
                  AND Ok = 1
                  AND CreatedAtMs >= @sinceMs
                  AND (@platform IS NULL OR Platform = @platform);
                """;
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@platform", (object?)platform ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sinceMs", since.ToUnixTimeMilliseconds());

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(result) > 0;
        }, ct);
    }

    public Task<IReadOnlyList<AlertRecord>> ListRecentAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<AlertRecord>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Kind, Platform, Target, Ok, Error, CreatedAtMs
                FROM Alerts
                ORDER BY Id DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = new List<AlertRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new AlertRecord(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4) != 0,
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
            }

            return list;
        }, ct);
    }
}
