using Serilog;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Turns;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>K-5：轮次审计表。</summary>
public sealed class TurnLogStoreTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly SqliteTurnLogStore _store;

    public TurnLogStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-turnlog-{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase($"Data Source={_dbPath}", Logger);
        _store = new SqliteTurnLogStore(_db);
    }

    public Task InitializeAsync() =>
        new DatabaseInitializer($"Data Source={_dbPath}", Logger).InitializeAsync();

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
    public async Task Append_then_list_recent_roundtrips()
    {
        var started = DateTimeOffset.UtcNow.AddSeconds(-5);

        await _store.AppendAsync(new TurnLog(0, "qq:p1", started, DateTimeOffset.UtcNow, "Suspended", 2, 3));

        var row = Assert.Single(await _store.ListRecentAsync());

        Assert.Equal("qq:p1", row.SessionId);
        Assert.Equal("Suspended", row.EndReason);
        Assert.Equal(2, row.SteerCount);
        Assert.Equal(3, row.FollowUpCount);
        Assert.InRange(row.StartedAt, started.AddSeconds(-1), started.AddSeconds(1));
    }
}
