using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Ai;

/// <summary><see cref="IToolCallStore"/> 的 SQLite 实现（表 <c>ToolCalls</c>，schema v19）。</summary>
public sealed class SqliteToolCallStore : IToolCallStore
{
    private readonly ISqliteDatabase _db;

    public SqliteToolCallStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task AddAsync(ToolCallEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Unit);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ToolCalls (Kind, Unit, Amount, SessionId, AtMs)
                VALUES (@kind, @unit, @amount, @sessionId, @atMs);
                """;
            cmd.Parameters.AddWithValue("@kind", entry.Kind);
            cmd.Parameters.AddWithValue("@unit", entry.Unit);
            cmd.Parameters.AddWithValue("@amount", entry.Amount);
            cmd.Parameters.AddWithValue("@sessionId", (object?)entry.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@atMs", entry.At.ToUnixTimeMilliseconds());

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<ToolUsageSummary>> GetSummaryAsync(
        int windowHours = 24, string? sessionId = null, CancellationToken ct = default)
    {
        return _db.ReadAsync<IReadOnlyList<ToolUsageSummary>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();

            // 两个可选条件都走「参数为空即不过滤」的写法：一份 SQL 覆盖四种组合，
            // 免得四份 SQL 里有一份忘了加 SessionId 过滤（统计口径悄悄变了没人发现）
            cmd.CommandText = """
                SELECT Kind,
                       Unit,
                       COUNT(*)      AS Calls,
                       COALESCE(SUM(Amount), 0) AS Total
                FROM ToolCalls
                WHERE (@sinceMs IS NULL OR AtMs >= @sinceMs)
                  AND (@sessionId IS NULL OR SessionId = @sessionId)
                GROUP BY Kind, Unit
                ORDER BY Total DESC, Calls DESC;
                """;

            object sinceParam = windowHours > 0
                ? DateTimeOffset.UtcNow.AddHours(-windowHours).ToUnixTimeMilliseconds()
                : DBNull.Value;

            cmd.Parameters.AddWithValue("@sinceMs", sinceParam);
            cmd.Parameters.AddWithValue(
                "@sessionId", string.IsNullOrWhiteSpace(sessionId) ? DBNull.Value : sessionId);

            var list = new List<ToolUsageSummary>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new ToolUsageSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetDouble(3)));
            }

            return list;
        }, ct);
    }
}
