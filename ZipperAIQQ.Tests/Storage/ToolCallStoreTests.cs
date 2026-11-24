using Serilog;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Ai;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>
/// 全家桶用量表（T2.7）：追加、按类别聚合、时间窗与会话过滤。
/// </summary>
/// <remarks>
/// 用临时文件库（与本目录其它仓储测试同口径）：<see cref="SqliteDatabase"/> 每次读写开新连接，
/// <c>:memory:</c> 会跟着连接消失。
/// </remarks>
public sealed class ToolCallStoreTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteToolCallStore _store;

    public ToolCallStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-toolcalls-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _store = new SqliteToolCallStore(_db);
    }

    public async Task InitializeAsync()
        => await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 临时文件删不掉不算失败
        }
    }

    [Fact]
    public async Task Empty_table_reports_nothing()
    {
        Assert.Empty(await _store.GetSummaryAsync());
    }

    [Fact]
    public async Task Aggregates_by_kind_and_unit()
    {
        await AddAsync("ocr", "page", 3);
        await AddAsync("ocr", "page", 2);
        await AddAsync("tts", "char", 120);

        var summary = await _store.GetSummaryAsync();

        Assert.Equal(2, summary.Count);
        var ocr = summary.Single(s => s.Kind == "ocr");
        Assert.Equal("page", ocr.Unit);
        Assert.Equal(2, ocr.Calls);
        Assert.Equal(5, ocr.TotalAmount);

        var tts = summary.Single(s => s.Kind == "tts");
        Assert.Equal(120, tts.TotalAmount);
        // 按用量降序：字符数大的排前面
        Assert.Equal("tts", summary[0].Kind);
    }

    [Fact]
    public async Task Session_filter_is_applied_only_when_given()
    {
        await AddAsync("ocr", "page", 1, "qq:p1");
        await AddAsync("ocr", "page", 4, "qq:p2");

        var all = await _store.GetSummaryAsync();
        var only = await _store.GetSummaryAsync(sessionId: "qq:p1");

        Assert.Equal(5, all.Single().TotalAmount);
        Assert.Equal(1, only.Single().TotalAmount);
    }

    [Fact]
    public async Task Window_excludes_old_rows()
    {
        await AddAsync("ocr", "page", 9);
        await InsertAtAsync("ocr", "page", 5, DateTimeOffset.UtcNow.AddHours(-48));

        var recent = await _store.GetSummaryAsync(windowHours: 24);
        var everything = await _store.GetSummaryAsync(windowHours: 0);

        Assert.Equal(9, recent.Single().TotalAmount);
        Assert.Equal(14, everything.Single().TotalAmount);
    }

    [Fact]
    public async Task Session_id_is_optional()
    {
        await AddAsync("tts", "char", 7, sessionId: null);

        var summary = await _store.GetSummaryAsync();
        Assert.Equal(7, summary.Single().TotalAmount);
    }

    private Task AddAsync(string kind, string unit, double amount, string? sessionId = "qq:p1") =>
        _store.AddAsync(new ToolCallEntry(kind, unit, amount, sessionId, DateTimeOffset.UtcNow));

    /// <summary>直接插一条「指定时间」的记录（仓储只能写 now，时间窗用例需要往回造）。</summary>
    private async Task InsertAtAsync(
        string kind, string unit, double amount, DateTimeOffset at)
    {
        await _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO ToolCalls (Kind, Unit, Amount, SessionId, AtMs) " +
                "VALUES (@k, @u, @a, NULL, @at);";
            cmd.Parameters.AddWithValue("@k", kind);
            cmd.Parameters.AddWithValue("@u", unit);
            cmd.Parameters.AddWithValue("@a", amount);
            cmd.Parameters.AddWithValue("@at", at.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        });
    }
}
