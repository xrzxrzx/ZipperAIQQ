using Serilog;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Alerts;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Owners;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>告警记录与主人账号镜像（D 批存储层）。</summary>
public sealed class AlertStoreTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-alerts-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteAlertStore _alerts;
    private readonly SqliteOwnerAccountStore _owners;

    public AlertStoreTests()
    {
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _alerts = new SqliteAlertStore(_db);
        _owners = new SqliteOwnerAccountStore(_db);
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

    private static AlertRecord Row(string kind, string? platform, bool ok, DateTimeOffset at) =>
        new(0, kind, platform, "10001", ok, ok ? null : "failed", at);

    [Fact]
    public async Task Only_successful_alerts_suppress_the_next_one()
    {
        var now = DateTimeOffset.UtcNow;

        await _alerts.AppendAsync(Row("ChannelDown", "qq", ok: false, now));
        Assert.False(await _alerts.ExistsRecentAsync("ChannelDown", "qq", now.AddMinutes(-10)));

        await _alerts.AppendAsync(Row("ChannelDown", "qq", ok: true, now));
        Assert.True(await _alerts.ExistsRecentAsync("ChannelDown", "qq", now.AddMinutes(-10)));
        Assert.False(await _alerts.ExistsRecentAsync("ChannelDown", "douyin", now.AddMinutes(-10)));
    }

    [Fact]
    public async Task Recent_list_is_newest_first()
    {
        var now = DateTimeOffset.UtcNow;
        await _alerts.AppendAsync(Row("ChannelDown", "qq", true, now.AddMinutes(-2)));
        await _alerts.AppendAsync(Row("ChannelRecovered", "qq", true, now));

        var rows = await _alerts.ListRecentAsync(10);

        Assert.Equal("ChannelRecovered", rows[0].Kind);
    }

    [Fact]
    public async Task Owner_accounts_upsert_is_idempotent()
    {
        var added = await _owners.UpsertAsync([new OwnerAccount("qq", "10001")]);
        var again = await _owners.UpsertAsync([new OwnerAccount("qq", "10001"), new OwnerAccount("douyin", "20002")]);

        Assert.Equal(1, added);
        Assert.Equal(1, again);

        var all = await _owners.ListAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, a => a.Platform == "douyin" && a.UserId == "20002");
    }
}
