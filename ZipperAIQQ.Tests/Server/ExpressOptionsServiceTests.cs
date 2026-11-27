using Serilog;
using ZipperAIQQ.Agent.Express;
using ZipperAIQQ.Server.Express;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>B6 T6.3：档位服务的读写、热更新与容错（表达档位设计 §7）。</summary>
public sealed class ExpressOptionsServiceTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-express-opts-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteRuntimeSettingsStore _store;

    public ExpressOptionsServiceTests()
    {
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _store = new SqliteRuntimeSettingsStore(_db);
    }

    public async Task InitializeAsync() =>
        await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 连接池可能还持有句柄；删不掉不影响结论
        }
    }

    private ExpressOptionsService NewService() => new(_store, Logger);

    [Fact]
    public async Task EmptyDatabase_UsesDefaults()
    {
        var service = NewService();
        await service.InitializeAsync();

        Assert.Equal(ExpressLevel.Daily, service.Current.Level);
        Assert.Equal(ExpressLevel.Explicit, service.Current.GroupCap);   // 「都开放」= 不额外限制
        Assert.True(service.Current.HygieneEnabled);
        Assert.True(service.Current.HygieneRegenerate);
        Assert.Empty(service.Current.SessionOverrides);
        Assert.Null(service.Current.CustomRules);
        Assert.Equal(0, service.HygieneHits);
    }

    [Fact]
    public async Task Update_PersistsAndSurvivesRestart()
    {
        var service = NewService();
        await service.InitializeAsync();

        var changed = await service.UpdateAsync(
            level: ExpressLevel.Close,
            groupCap: ExpressLevel.Daily,
            sessionId: null,
            sessionLevel: null,
            promptExpress: null,
            hygieneEnabled: null,
            hygieneRegenerate: null,
            actor: "http-client");

        Assert.Equal(["level", "groupCap"], changed);

        // 重启 = 新实例重新 Initialize（模拟服务端重启）
        var restarted = NewService();
        await restarted.InitializeAsync();

        Assert.Equal(ExpressLevel.Close, restarted.Current.Level);
        Assert.Equal(ExpressLevel.Daily, restarted.Current.GroupCap);
        Assert.Equal("http-client", restarted.Current.UpdatedBy);
        Assert.True(restarted.Current.UpdatedAtMs > 0);
    }

    [Fact]
    public async Task Update_SameValue_ReportsNoChange()
    {
        var service = NewService();
        await service.InitializeAsync();

        var changed = await service.UpdateAsync(
            ExpressLevel.Daily, null, null, null, null, null, null, "http-client");

        Assert.Empty(changed);
    }

    [Fact]
    public async Task SessionOverride_CanBeSetAndCleared()
    {
        var service = NewService();
        await service.InitializeAsync();

        var set = await service.UpdateAsync(
            null, null, "douyin:p1000000000", ExpressLevel.Explicit, null, null, null, "http-client");

        Assert.Single(set);
        Assert.Equal(ExpressLevel.Explicit, service.Current.SessionOverrides["douyin:p1000000000"]);

        var cleared = await service.UpdateAsync(
            null, null, "douyin:p1000000000", null, null, null, null, "http-client");

        Assert.Single(cleared);
        Assert.Empty(service.Current.SessionOverrides);
    }

    [Fact]
    public async Task PromptExpress_EmptyString_RestoresBuiltInDefault()
    {
        var service = NewService();
        await service.InitializeAsync();

        await service.UpdateAsync(
            null, null, null, null, "按我的规矩说话。", null, null, "http-client");
        Assert.Equal("按我的规矩说话。", service.Current.CustomRules);

        await service.UpdateAsync(null, null, null, null, "   ", null, null, "http-client");
        Assert.Null(service.Current.CustomRules);

        // 重新 Initialize 后仍应是「用内置默认」（库里存的是空串）
        var restarted = NewService();
        await restarted.InitializeAsync();
        Assert.Null(restarted.Current.CustomRules);
    }

    [Fact]
    public async Task BrokenJson_FallsBackToDefaultsWithoutThrowing()
    {
        await _store.SetAsync(ExpressOptionsService.StorageKey, "{ not json");
        await _store.SetAsync(ExpressOptionsService.LegacyPromptKey, "自定义规则");

        var service = NewService();
        await service.InitializeAsync();   // 不抛

        Assert.Equal(ExpressLevel.Daily, service.Current.Level);
        Assert.Equal(ExpressLevel.Explicit, service.Current.GroupCap);
        Assert.Equal("自定义规则", service.Current.CustomRules);   // 规则段是独立键，坏 JSON 不影响它
    }

    [Fact]
    public async Task UnknownLevelValue_IsIgnoredPerFieldNotWholePayload()
    {
        await _store.SetAsync(
            ExpressOptionsService.StorageKey,
            """{"level":"crazy","groupCap":"close","hygiene":{"enabled":false,"regenerate":true}}""");

        var service = NewService();
        await service.InitializeAsync();

        Assert.Equal(ExpressLevel.Daily, service.Current.Level);    // 认不出 → 默认
        Assert.Equal(ExpressLevel.Close, service.Current.GroupCap); // 认得的那项照用
        Assert.False(service.Current.HygieneEnabled);
    }

    [Fact]
    public async Task HygieneCounters_Accumulate()
    {
        var service = NewService();
        await service.InitializeAsync();

        service.RecordHygieneHit(ExpressHygieneCategory.Refusal);
        service.RecordHygieneHit(ExpressHygieneCategory.Hedging);
        service.RecordHygieneRegeneration();

        Assert.Equal(2, service.HygieneHits);
        Assert.Equal(1, service.HygieneRegenerated);
    }

    [Fact]
    public async Task LegacyPrompt_MigratesOnlyWhenExpressKeyMissing()
    {
        // 老部署：只有 prompt.express，没有 express 键
        await _store.SetAsync(ExpressOptionsService.LegacyPromptKey, "老规则段");

        var service = NewService();
        await service.InitializeAsync();
        Assert.Equal("老规则段", service.Current.CustomRules);

        // 用户点了「恢复默认规则段」
        await service.UpdateAsync(null, null, null, null, string.Empty, null, null, "http-client");
        Assert.Null(service.Current.CustomRules);

        // 重启：老键**不许**把值搬回来（否则表现成「恢复默认没生效」，且每次重启都重写一次）
        var restarted = NewService();
        await restarted.InitializeAsync();
        Assert.Null(restarted.Current.CustomRules);
    }

    [Fact]
    public async Task ApplySnapshot_RestoresWholeState()
    {
        var service = NewService();
        await service.InitializeAsync();

        var snapshot = new ExpressSettings
        {
            Level = ExpressLevel.Close,
            GroupCap = ExpressLevel.Daily,
            SessionOverrides = new Dictionary<string, ExpressLevel>(StringComparer.Ordinal)
            {
                ["qq:g10001"] = ExpressLevel.Daily,
            },
            HygieneEnabled = false,
            HygieneRegenerate = false,
            CustomRules = "快照里的规则段",
        };

        await service.ApplySnapshotAsync(snapshot, "http-client");

        var restarted = NewService();
        await restarted.InitializeAsync();

        Assert.Equal(ExpressLevel.Close, restarted.Current.Level);
        Assert.Equal(ExpressLevel.Daily, restarted.Current.GroupCap);
        Assert.Equal(ExpressLevel.Daily, restarted.Current.SessionOverrides["qq:g10001"]);
        Assert.False(restarted.Current.HygieneEnabled);
        Assert.False(restarted.Current.HygieneRegenerate);
        Assert.Equal("快照里的规则段", restarted.Current.CustomRules);
    }
}

