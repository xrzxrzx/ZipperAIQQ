using Serilog;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Personas;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>
/// 会话级人设的「启用 / 停用」（问题记录 #16）。
/// </summary>
public sealed class PersonaEnabledTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly DefaultPersonaStore _store;
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();
    private static readonly Persona Global = new("全局", "【形象】\n全局形象");

    public PersonaEnabledTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-persona-{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase($"Data Source={_dbPath}", Logger);
        _store = new DefaultPersonaStore(_db, Global);
    }

    public async Task InitializeAsync()
        => await new DatabaseInitializer($"Data Source={_dbPath}", Logger).InitializeAsync();

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

    private static Persona Session => new("会话", "【形象】\n会话形象");

    [Fact]
    public async Task Disabled_override_falls_back_to_global()
    {
        await _store.SetAsync("qq:p1", Session);
        await _store.SetEnabledAsync("qq:p1", false);

        var effective = await _store.GetAsync("qq", "Private", "1");
        Assert.Equal(Global.Content, effective.Content);

        var state = await _store.GetStateAsync("qq:p1");
        Assert.NotNull(state.Override);
        Assert.False(state.Enabled);
    }

    [Fact]
    public async Task Re_enabled_override_takes_effect_again()
    {
        await _store.SetAsync("qq:p1", Session);
        await _store.SetEnabledAsync("qq:p1", false);
        await _store.SetEnabledAsync("qq:p1", true);

        var effective = await _store.GetAsync("qq", "Private", "1");
        Assert.Equal(Session.Content, effective.Content);
    }

    [Fact]
    public async Task No_override_reports_enabled_without_content()
    {
        var state = await _store.GetStateAsync("qq:p9");

        Assert.Null(state.Override);
        Assert.True(state.Enabled);
        Assert.Equal(Global.Content, (await _store.GetAsync("qq", "Private", "9")).Content);
    }

    [Fact]
    public async Task Updating_content_keeps_enabled_flag()
    {
        await _store.SetAsync("qq:p1", Session);
        await _store.SetEnabledAsync("qq:p1", false);
        await _store.SetAsync("qq:p1", new Persona("会话2", "【形象】\n改了"));

        Assert.False(await _store.IsEnabledAsync("qq:p1"));
    }
}
