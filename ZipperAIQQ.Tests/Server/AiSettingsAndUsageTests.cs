using Serilog;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Server.Ai;
using ZipperAIQQ.Storage.Ai;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// T2.7 的两块接线：<see cref="ToolUsageSink"/>（记账不能拖垮工具）与
/// <see cref="DialogEnhanceService"/>（开关落库 + 读回）。
/// </summary>
public sealed class AiSettingsAndUsageTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteToolCallStore _toolCalls;
    private readonly SqliteRuntimeSettingsStore _settings;

    public AiSettingsAndUsageTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-ai-settings-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _toolCalls = new SqliteToolCallStore(_db);
        _settings = new SqliteRuntimeSettingsStore(_db);
    }

    public async Task InitializeAsync()
        => await new ZipperAIQQ.Storage.DatabaseInitializer(_connectionString, Logger).InitializeAsync();

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

    // ── 记账 ──

    [Fact]
    public async Task Sink_writes_one_row_per_call()
    {
        var sink = new ToolUsageSink(_toolCalls, Logger);

        await sink.RecordAsync("ocr", "page", 2, "qq:p1");
        await sink.RecordAsync("tts", "char", 30, "qq:p1");

        var summary = await _toolCalls.GetSummaryAsync();
        Assert.Equal(2, summary.Count);
        Assert.Equal(2, summary.Single(s => s.Kind == "ocr").TotalAmount);
        Assert.Equal(30, summary.Single(s => s.Kind == "tts").TotalAmount);
    }

    [Fact]
    public async Task Sink_swallows_store_failures_so_a_tool_result_is_never_lost()
    {
        var sink = new ToolUsageSink(new ThrowingStore(), Logger);

        // 不抛就是通过：记账是观测，工具结果是交付
        await sink.RecordAsync("ocr", "page", 1, "qq:p1");
    }

    [Fact]
    public async Task Sink_ignores_blank_kind_or_unit()
    {
        var sink = new ToolUsageSink(_toolCalls, Logger);

        await sink.RecordAsync("", "page", 1, null);
        await sink.RecordAsync("ocr", " ", 1, null);

        Assert.Empty(await _toolCalls.GetSummaryAsync());
    }

    // ── 对话增强开关 ──

    [Fact]
    public async Task Enhance_defaults_to_all_off()
    {
        var service = new DialogEnhanceService(_settings, Logger);

        await service.InitializeAsync();

        Assert.False(service.Current.AnyEnabled);
        Assert.Contains("全关", service.Current.Describe());
    }

    [Fact]
    public async Task Enhance_persists_and_reloads_from_the_database()
    {
        var service = new DialogEnhanceService(_settings, Logger);
        await service.InitializeAsync();

        var changed = await service.UpdateAsync(
            webSearch: true, readUrl: false, safetyCheckHigh: true, title: false);

        Assert.True(changed);
        Assert.True(service.Current.WebSearch);
        Assert.True(service.Current.SafetyCheckHigh);
        Assert.False(service.Current.Title);

        // 新实例（= 重启）读回来还是同一份状态
        var reloaded = new DialogEnhanceService(_settings, Logger);
        await reloaded.InitializeAsync();

        Assert.True(reloaded.Current.WebSearch);
        Assert.True(reloaded.Current.SafetyCheckHigh);
        Assert.False(reloaded.Current.ReadUrl);
    }

    [Fact]
    public async Task Enhance_reports_no_change_when_nothing_changed()
    {
        var service = new DialogEnhanceService(_settings, Logger);
        await service.InitializeAsync();

        Assert.False(await service.UpdateAsync(false, false, false, false));
    }

    [Fact]
    public void Description_warns_about_ignored_sampling_parameters()
    {
        var describe = new ZipperAIQQ.Agent.Llm.DeepSeekEnhanceOptions { WebSearch = true }.Describe();

        Assert.Contains("采样参数", describe);
        Assert.Contains("联网搜索", describe);
    }

    [Fact]
    public async Task Enhance_migrates_the_four_legacy_keys_into_one()
    {
        // 老版本四个键各写一次；服务端应能读出来、并迁移成单键（老键不删）
        await _settings.SetAsync("ai.dialogEnhance.webSearch", "true");
        await _settings.SetAsync("ai.dialogEnhance.title", "true");

        var service = new DialogEnhanceService(_settings, Logger);
        await service.InitializeAsync();

        Assert.True(service.Current.WebSearch);
        Assert.True(service.Current.Title);
        Assert.False(service.Current.SafetyCheckHigh);

        var migrated = await _settings.GetAsync(DialogEnhanceService.StorageKey);
        Assert.False(string.IsNullOrWhiteSpace(migrated));
        Assert.Contains("webSearch", migrated);
        // 老键保留（人工恢复用），不被清掉
        Assert.Equal("true", await _settings.GetAsync("ai.dialogEnhance.webSearch"));
    }

    [Fact]
    public async Task Enhance_corrupt_storage_falls_back_to_all_off()
    {
        await _settings.SetAsync(DialogEnhanceService.StorageKey, "{not json");

        var service = new DialogEnhanceService(_settings, Logger);
        await service.InitializeAsync();

        // 宁可少开，不可因为一份坏 JSON 意外打开内容安全/联网
        Assert.False(service.Current.AnyEnabled);
    }

    private sealed class ThrowingStore : IToolCallStore
    {
        public Task AddAsync(ToolCallEntry entry, CancellationToken ct = default) =>
            throw new InvalidOperationException("模拟写库失败");

        public Task<IReadOnlyList<ToolUsageSummary>> GetSummaryAsync(
            int windowHours = 24, string? sessionId = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("模拟读库失败");
    }
}
