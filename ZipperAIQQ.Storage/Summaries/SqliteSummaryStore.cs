using System.Globalization;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Summaries;

/// <summary><see cref="ISummaryStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteSummaryStore : ISummaryStore
{
    private readonly ISqliteDatabase _db;

    public SqliteSummaryStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<SessionSummary?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _db.ReadAsync<SessionSummary?>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT SessionId, Summary, CoversUpToId, UpdatedAtMs
                FROM SessionSummaries
                WHERE SessionId = @sid;
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            return new SessionSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
        }, ct);
    }

    public Task SaveAsync(
        string sessionId,
        string summary,
        long coversUpToId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(summary);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SessionSummaries (SessionId, Summary, CoversUpToId, UpdatedAtMs)
                VALUES (@sid, @summary, @cover, @now)
                ON CONFLICT(SessionId) DO UPDATE SET
                    Summary      = excluded.Summary,
                    CoversUpToId = excluded.CoversUpToId,
                    UpdatedAtMs  = excluded.UpdatedAtMs;
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@summary", summary);
            cmd.Parameters.AddWithValue("@cover", coversUpToId);
            cmd.Parameters.AddWithValue("@now", now);

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }
}
