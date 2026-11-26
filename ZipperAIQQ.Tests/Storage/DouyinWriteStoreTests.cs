using Serilog;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Douyin;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>
/// 抖音写动作表（B5 / schema v21）：追加、时间窗计数、同人一次、最近列表。
/// </summary>
/// <remarks>
/// 用临时文件库（与本目录其它仓储测试同口径）：<see cref="SqliteDatabase"/> 每次读写开新连接，
/// <c>:memory:</c> 会跟着连接消失。
/// </remarks>
public sealed class DouyinWriteStoreTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteDouyinWriteStore _store;

    public DouyinWriteStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-douyinwrites-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _store = new SqliteDouyinWriteStore(_db);
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

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
    public async Task Counts_only_rows_inside_the_window()
    {
        await Add("commentReply", "a:甲", Now.AddHours(-30));
        await Add("commentReply", "a:乙", Now.AddHours(-2));
        await Add("commentReply", "a:丙", Now);

        var count = await _store.CountSinceAsync("commentReply", Now.AddHours(-24));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Oldest_since_tells_when_quota_comes_back()
    {
        var first = Now.AddHours(-5);
        await Add("commentReply", "a:甲", first);
        await Add("commentReply", "a:乙", Now.AddHours(-1));

        var oldest = await _store.OldestSinceAsync("commentReply", Now.AddHours(-24));

        Assert.NotNull(oldest);
        Assert.Equal(first.ToUnixTimeSeconds(), oldest!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Last_at_reads_the_newest_row()
    {
        await Add("commentReply", "a:甲", Now.AddMinutes(-30));
        await Add("commentReply", "a:乙", Now.AddMinutes(-2));

        var last = await _store.LastAtAsync("commentReply");

        Assert.NotNull(last);
        Assert.True(last!.Value > Now.AddMinutes(-3));
    }

    [Fact]
    public async Task Same_target_detection_only_counts_successful_rows()
    {
        // 失败的尝试**不该**把这个人锁掉一整天：他确实还没被回过
        await Add("commentReply", "a:甲", Now.AddHours(-1), ok: false);
        Assert.False(await _store.ExistsForTargetAsync("commentReply", "a:甲", Now.AddHours(-24)));

        await Add("commentReply", "a:甲", Now);
        Assert.True(await _store.ExistsForTargetAsync("commentReply", "a:甲", Now.AddHours(-24)));
    }

    [Fact]
    public async Task Actions_do_not_share_counters()
    {
        await Add("commentReply", "a:甲", Now);
        await Add("commentTop", "a:甲", Now);

        Assert.Equal(1, await _store.CountSinceAsync("commentReply", Now.AddHours(-24)));
        Assert.Equal(1, await _store.CountSinceAsync("commentTop", Now.AddHours(-24)));
    }

    [Fact]
    public async Task Recent_list_is_newest_first()
    {
        await Add("commentReply", "a:甲", Now.AddMinutes(-10));
        await Add("commentReply", "a:乙", Now);

        var recent = await _store.ListRecentAsync(10);

        Assert.Equal(2, recent.Count);
        Assert.Equal("a:乙", recent[0].TargetKey);
        Assert.True(recent[0].Ok);
    }

    private Task<long> Add(
        string action, string targetKey, DateTimeOffset at, bool ok = true) =>
        _store.AppendAsync(new DouyinWriteAction(
            Action: action,
            TargetKey: targetKey,
            Ok: ok,
            At: at,
            ItemId: "7706296015465071715",
            CommentId: "7412345678901234567",
            Note: ok ? "readback" : "denied:配额"));
}
