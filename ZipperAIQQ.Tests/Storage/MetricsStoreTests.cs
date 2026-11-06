using Microsoft.Data.Sqlite;
using Serilog;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>
/// 调用指标：缓存命中列、会话维度、旧库迁移。
/// </summary>
/// <remarks>
/// 用临时文件库（与 <see cref="SqliteMessageStoreTests"/> 同口径）：
/// <see cref="SqliteDatabase"/> 每次读写开新连接，<c>:memory:</c> 会跟着连接消失。
/// </remarks>
public sealed class MetricsStoreTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteMetricsStore _store;

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    public MetricsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-metrics-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _store = new SqliteMetricsStore(_db);
    }

    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Cached_tokens_are_summed_per_kind()
    {
        await _store.RecordAsync(new MetricEntry("qq:p1", "llm.call", 100, 1000, 200, 600));
        await _store.RecordAsync(new MetricEntry("qq:p1", "llm.call", 300, 500, 100, 400));

        var rows = await _store.GetSummaryAsync(0);

        var llm = Assert.Single(rows);
        Assert.Equal("llm.call", llm.Kind);
        Assert.Equal(1500, llm.TotalInputTokens);
        Assert.Equal(1000, llm.TotalCachedInputTokens);
    }

    [Fact]
    public async Task Null_cached_tokens_count_as_zero()
    {
        // 旧行 / 非 LLM 调用没有缓存概念：SUM 不能因为 NULL 变成 NULL
        await _store.RecordAsync(new MetricEntry("qq:p1", "tool.send_message", 10));
        await _store.RecordAsync(new MetricEntry("qq:p1", "llm.call", 50, 100, 20, null));

        var rows = await _store.GetSummaryAsync(0);

        Assert.Equal(0, rows.Single(r => r.Kind == "tool.send_message").TotalCachedInputTokens);
        Assert.Equal(0, rows.Single(r => r.Kind == "llm.call").TotalCachedInputTokens);
    }

    [Fact]
    public async Task Session_filter_only_counts_that_session()
    {
        await _store.RecordAsync(new MetricEntry("qq:p1", "llm.call", 100, 100, 10));
        await _store.RecordAsync(new MetricEntry("qq:p2", "llm.call", 200, 200, 20));
        await _store.RecordAsync(new MetricEntry("qq:p2", "tool.send_message", 30));

        var onlyP2 = await _store.GetSummaryAsync(0, "qq:p2");
        Assert.Equal(2, onlyP2.Count);
        Assert.DoesNotContain(onlyP2, r => r.Kind == "llm.call" && r.Count > 1);

        var p2Llm = onlyP2.Single(r => r.Kind == "llm.call");
        Assert.Equal(1, p2Llm.Count);
        Assert.Equal(200, p2Llm.TotalInputTokens);

        var all = await _store.GetSummaryAsync(0);
        Assert.Equal(2, all.Single(r => r.Kind == "llm.call").Count);
    }

    [Fact]
    public async Task Percentiles_use_nearest_rank()
    {
        foreach (var ms in new double[] { 100, 200, 300, 400, 500 })
        {
            await _store.RecordAsync(new MetricEntry("qq:p1", "llm.call", ms));
        }

        var llm = Assert.Single(await _store.GetSummaryAsync(0));

        // 最近秩法：p50 → 第 ceil(5*0.5)=3 条；p95 → 第 ceil(5*0.95)=5 条
        Assert.Equal(300, llm.P50Ms);
        Assert.Equal(500, llm.P95Ms);
    }

    [Fact]
    public async Task Frequent_kinds_come_before_long_tail()
    {
        // 旧排序（只按 AVG 倒序）会让「偶尔跑一次但很慢」的类别霸占表头（问题记录 04 #3）
        for (var i = 0; i < 10; i++)
        {
            await _store.RecordAsync(new MetricEntry("qq:p1", "llm.call", 1000));
        }

        await _store.RecordAsync(new MetricEntry("qq:p1", "summary", 9000));
        await _store.RecordAsync(new MetricEntry("qq:p1", "summary", 11000));

        var rows = await _store.GetSummaryAsync(0);

        Assert.Equal("llm.call", rows[0].Kind);
        Assert.Equal(11000, rows.Single(r => r.Kind == "summary").P95Ms);
    }

    [Fact]
    public async Task List_sessions_returns_distinct_sessions_recent_first()
    {
        await _store.RecordAsync(new MetricEntry("qq:p1", "llm.call", 10, 1, 1));
        await Task.Delay(5);
        await _store.RecordAsync(new MetricEntry("qq:g2", "llm.call", 10, 1, 1));
        await _store.RecordAsync(new MetricEntry("qq:g2", "tool.send_message", 10));
        await _store.RecordAsync(new MetricEntry(null, "embedding.backfill", 10));

        var sessions = await _store.ListSessionAsync(0);

        Assert.Equal(["qq:g2", "qq:p1"], sessions.Select(s => s.SessionId).ToArray());
        Assert.Equal(2, sessions[0].Calls);
    }

    [Fact]
    public async Task Old_database_is_migrated_to_current_version()
    {
        // 造一个 v10 的库：CallMetrics 没有 CachedInputTokens
        var path = Path.Combine(Path.GetTempPath(), $"zipper-v10-{Guid.NewGuid():N}.db");
        var conn = $"Data Source={path}";

        await using (var connection = new SqliteConnection(conn))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE CallMetrics (
                    Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    SessionId    TEXT,
                    Kind         TEXT    NOT NULL,
                    ElapsedMs    REAL    NOT NULL,
                    InputTokens  INTEGER,
                    OutputTokens INTEGER,
                    CreatedAtMs  INTEGER NOT NULL
                );
                CREATE TABLE SchemaVersion (Version INTEGER NOT NULL);
                INSERT INTO SchemaVersion (Version) VALUES (10);
                INSERT INTO CallMetrics (SessionId, Kind, ElapsedMs, InputTokens, OutputTokens, CreatedAtMs)
                VALUES ('qq:p1', 'llm.call', 12.5, 100, 10, 1700000000000);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            await new DatabaseInitializer(conn, Logger).InitializeAsync();

            await using var check = new SqliteConnection(conn);
            await check.OpenAsync();

            await using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('CallMetrics') WHERE name = 'CachedInputTokens';";
                Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
            }

            await using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "SELECT Version FROM SchemaVersion LIMIT 1;";
                Assert.Equal((long)DatabaseInitializer.CurrentSchemaVersion, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
            }

            await using (var cmd = check.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM CallMetrics;";
                Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
            }
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
