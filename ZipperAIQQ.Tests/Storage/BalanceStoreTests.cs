using Serilog;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Billing;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>余额采样存储：窗口查询与最近一条。</summary>
public sealed class BalanceStoreTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 0, 0, 0, TimeSpan.FromHours(8));

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-balance-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteBalanceStore _store;

    public BalanceStoreTests()
    {
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _store = new SqliteBalanceStore(_db);
    }

    public async Task InitializeAsync() => await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();

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

    private static BalanceSample Sample(decimal total, int minutes) =>
        new("deepseek", "CNY", total, 0, 100, true, Start.AddMinutes(minutes));

    [Fact]
    public async Task Samples_are_listed_in_time_order_and_latest_wins()
    {
        await _store.AppendAsync(Sample(100, 0));
        await _store.AppendAsync(Sample(99, 10));

        var rows = await _store.ListSinceAsync("deepseek", Start.AddMinutes(-1));

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].SampledAt <= rows[1].SampledAt);
        Assert.Equal(99m, (await _store.LatestAsync("deepseek"))!.Total);
    }

    [Fact]
    public async Task Purge_removes_only_old_samples()
    {
        await _store.AppendAsync(Sample(100, 0));
        await _store.AppendAsync(Sample(99, 10));

        var removed = await _store.PurgeBeforeAsync(Start.AddMinutes(5));

        Assert.Equal(1, removed);
        Assert.Single(await _store.ListSinceAsync("deepseek", Start.AddMinutes(-1)));
    }

    [Fact]
    public async Task Since_filter_excludes_old_samples()
    {
        await _store.AppendAsync(Sample(100, 0));
        await _store.AppendAsync(Sample(99, 10));

        var rows = await _store.ListSinceAsync("deepseek", Start.AddMinutes(5));

        Assert.Single(rows);
        Assert.Equal(99m, rows[0].Total);
    }
}
