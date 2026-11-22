using Microsoft.Data.Sqlite;
using Serilog;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>
/// API key 槽位表（问题记录 06 §5）：写入 / 覆盖 / 读取 / 删除，以及「只动自己的槽位」。
/// </summary>
/// <remarks>
/// 用临时文件库（与 <see cref="MetricsStoreTests"/> 同口径）：
/// <see cref="SqliteDatabase"/> 每次读写开新连接，<c>:memory:</c> 会跟着连接消失。
/// </remarks>
public sealed class ApiKeyStoreTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteApiKeyStore _store;

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    public ApiKeyStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-apikeys-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _store = new SqliteApiKeyStore(_db);
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
            // 临时文件删不掉不算测试失败
        }
    }

    [Fact]
    public async Task Empty_store_has_no_keys()
    {
        Assert.Empty(await _store.ListAsync());
        Assert.Null(await _store.GetAsync(0));
    }

    [Fact]
    public async Task Upsert_then_read_roundtrips_the_cipher()
    {
        await _store.UpsertAsync(0, "cipher-a");

        var stored = await _store.GetAsync(0);
        Assert.NotNull(stored);
        Assert.Equal("cipher-a", stored!.KeyCipher);
        Assert.Equal(0, stored.Slot);
    }

    [Fact]
    public async Task Upsert_overwrites_same_slot()
    {
        await _store.UpsertAsync(0, "old");
        await _store.UpsertAsync(0, "new");

        Assert.Equal("new", (await _store.GetAsync(0))!.KeyCipher);
        Assert.Single(await _store.ListAsync());
    }

    [Fact]
    public async Task Writing_one_slot_keeps_the_others()
    {
        await _store.UpsertAsync(0, "primary");
        await _store.UpsertAsync(1, "archived");

        await _store.UpsertAsync(0, "rotated");

        var all = await _store.ListAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("rotated", all[0].KeyCipher);
        Assert.Equal("archived", all[1].KeyCipher);
    }

    [Fact]
    public async Task List_is_ordered_by_slot()
    {
        await _store.UpsertAsync(2, "third");
        await _store.UpsertAsync(0, "first");
        await _store.UpsertAsync(1, "second");

        var all = await _store.ListAsync();
        Assert.Equal([0, 1, 2], all.Select(k => k.Slot));
    }

    [Fact]
    public async Task Delete_removes_only_that_slot()
    {
        await _store.UpsertAsync(0, "a");
        await _store.UpsertAsync(1, "b");

        await _store.DeleteAsync(0);

        Assert.Null(await _store.GetAsync(0));
        Assert.NotNull(await _store.GetAsync(1));
    }

    [Fact]
    public async Task Negative_slot_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.GetAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.UpsertAsync(-1, "x"));
    }

    [Fact]
    public async Task Blank_cipher_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpsertAsync(0, "   "));
    }

    [Fact]
    public async Task Table_exists_on_a_freshly_initialized_database()
    {
        // schema v19 的回归：ApiKeys 表必须随初始化一起建出来，否则升级后的第一把 key 无处可存
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'ApiKeys';";
        var name = await cmd.ExecuteScalarAsync();

        Assert.Equal("ApiKeys", name);
    }

    [Fact]
    public async Task Old_database_without_the_table_gets_it_on_next_initialization()
    {
        // 模拟「v18 旧库」：建表都是无条件的 CREATE TABLE IF NOT EXISTS，
        // 所以把表删掉再初始化一次，必须被补回来（不是靠版本号门控）
        await using (var conn = new SqliteConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var drop = conn.CreateCommand();
            drop.CommandText = "DROP TABLE ApiKeys;";
            await drop.ExecuteNonQueryAsync();
        }

        await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();

        Assert.Empty(await _store.ListAsync());
        await _store.UpsertAsync(0, "after-upgrade");
        Assert.Equal("after-upgrade", (await _store.GetAsync(0))!.KeyCipher);
    }
}
