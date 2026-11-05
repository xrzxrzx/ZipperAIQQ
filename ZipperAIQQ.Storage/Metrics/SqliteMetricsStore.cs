using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Metrics;

/// <summary><see cref="IMetricsStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteMetricsStore : IMetricsStore
{
    private readonly ISqliteDatabase _db;

    public SqliteMetricsStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task RecordAsync(MetricEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Kind);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO CallMetrics (SessionId, Kind, ElapsedMs, InputTokens, OutputTokens, CachedInputTokens, CreatedAtMs)
                VALUES (@sid, @kind, @elapsed, @in, @out, @cached, @now);
                """;
            cmd.Parameters.AddWithValue("@sid", (object?)entry.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@kind", entry.Kind);
            cmd.Parameters.AddWithValue("@elapsed", entry.ElapsedMs);
            cmd.Parameters.AddWithValue("@in", (object?)entry.InputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@out", (object?)entry.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cached", (object?)entry.CachedInputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@now", now);

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<MetricSummary>> GetSummaryAsync(
        int windowHours = 24,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var since = windowHours > 0
            ? DateTimeOffset.UtcNow.AddHours(-windowHours).ToUnixTimeMilliseconds()
            : 0;

        var hasSession = !string.IsNullOrWhiteSpace(sessionId);

        return _db.ReadAsync<IReadOnlyList<MetricSummary>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            // 分位数用窗口函数算（最近秩法）：
            //   p = 排序后第 ceil(n·q) 条 —— 等价于 rn = CAST(n*q AS INTEGER) + 1（整数乘法定点）。
            // SQLite 3.25+ 支持窗口函数，本仓捆绑的 e_sqlite3 满足。
            //
            // 排序改成「次数降序、平均降序」：旧版只按 AVG 倒序，会让偶尔跑一次的长尾
            // 固定占住表头（问题记录 04 #3）。长尾交给 p95 表达。
            cmd.CommandText = """
                WITH ranked AS (
                    SELECT Kind, ElapsedMs, InputTokens, OutputTokens, CachedInputTokens,
                           ROW_NUMBER() OVER (PARTITION BY Kind ORDER BY ElapsedMs) AS rn,
                           COUNT(*) OVER (PARTITION BY Kind) AS cnt
                    FROM CallMetrics
                    WHERE (@since = 0 OR CreatedAtMs >= @since)
                      AND (@sid IS NULL OR SessionId = @sid)
                )
                SELECT Kind,
                       COUNT(*) AS Cnt,
                       AVG(ElapsedMs),
                       MIN(ElapsedMs),
                       MAX(ElapsedMs),
                       COALESCE(SUM(InputTokens), 0),
                       COALESCE(SUM(OutputTokens), 0),
                       COALESCE(SUM(CachedInputTokens), 0),
                       COALESCE(MAX(CASE WHEN rn = CAST(cnt * 0.50 AS INTEGER) + 1 THEN ElapsedMs END), 0),
                       COALESCE(MAX(CASE WHEN rn = CAST(cnt * 0.95 AS INTEGER) + 1 THEN ElapsedMs END), 0)
                FROM ranked
                GROUP BY Kind
                ORDER BY COUNT(*) DESC, AVG(ElapsedMs) DESC;
                """;
            cmd.Parameters.AddWithValue("@since", since);
            cmd.Parameters.AddWithValue("@sid", hasSession ? sessionId! : DBNull.Value);

            var list = new List<MetricSummary>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new MetricSummary(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetDouble(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetDouble(8),
                    reader.GetDouble(9)));
            }

            return (IReadOnlyList<MetricSummary>)list;
        }, ct);
    }

    public Task<IReadOnlyList<MetricSessionInfo>> ListSessionAsync(
        int windowHours = 24,
        CancellationToken ct = default)
    {
        var since = windowHours > 0
            ? DateTimeOffset.UtcNow.AddHours(-windowHours).ToUnixTimeMilliseconds()
            : 0;

        return _db.ReadAsync<IReadOnlyList<MetricSessionInfo>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT SessionId, COUNT(*), MAX(CreatedAtMs)
                FROM CallMetrics
                WHERE SessionId IS NOT NULL AND SessionId <> ''
                  AND (@since = 0 OR CreatedAtMs >= @since)
                GROUP BY SessionId
                ORDER BY MAX(CreatedAtMs) DESC
                LIMIT 200;
                """;
            cmd.Parameters.AddWithValue("@since", since);

            var list = new List<MetricSessionInfo>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new MetricSessionInfo(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
            }

            return (IReadOnlyList<MetricSessionInfo>)list;
        }, ct);
    }
}
