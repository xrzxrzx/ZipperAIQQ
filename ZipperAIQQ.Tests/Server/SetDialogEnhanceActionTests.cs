using System.Text.Json;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Server.Ai;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 「对话增强」保存动作（T2.6）：改动要重建客户端、无变化不重建、
/// **重建失败必须如实回报**（否则界面显示"已生效"而实际还在用旧客户端）。
/// </summary>
public sealed class SetDialogEnhanceActionTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteRuntimeSettingsStore _settings;
    private readonly DialogEnhanceService _service;

    public SetDialogEnhanceActionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-enhance-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _settings = new SqliteRuntimeSettingsStore(_db);
        _service = new DialogEnhanceService(_settings, Logger);
    }

    public async Task InitializeAsync()
    {
        await new ZipperAIQQ.Storage.DatabaseInitializer(_connectionString, Logger).InitializeAsync();
        await _service.InitializeAsync();
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
            // 临时文件删不掉不算失败
        }
    }

    [Fact]
    public async Task Change_rebuilds_the_client_and_reports_effective()
    {
        var clients = new FakeClients(result: true);
        var action = new SetDialogEnhanceAction(_service, clients, Logger);

        var result = await action.HandleAsync(
            Payload(new { webSearch = true }), CancellationToken.None);

        Assert.Equal(1, clients.Reloads);
        Assert.Contains("\"effective\":true", Json(result), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("webSearch", Json(result), StringComparison.OrdinalIgnoreCase);
        Assert.True(_service.Current.WebSearch);
    }

    [Fact]
    public async Task No_change_does_not_rebuild()
    {
        var clients = new FakeClients(result: true);
        var action = new SetDialogEnhanceAction(_service, clients, Logger);

        var result = await action.HandleAsync(
            Payload(new { webSearch = false }), CancellationToken.None);

        Assert.Equal(0, clients.Reloads);
        Assert.Contains("\"skipped\":true", Json(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_rebuild_is_reported_instead_of_pretending_success()
    {
        // 关键回归：以前 ReloadAsync 是 void，这里只能显示"已保存"，用户以为生效了
        var clients = new FakeClients(result: false);
        var action = new SetDialogEnhanceAction(_service, clients, Logger);

        var result = await action.HandleAsync(
            Payload(new { title = true }), CancellationToken.None);

        using var doc = JsonDocument.Parse(Json(result));
        Assert.Equal(1, clients.Reloads);
        Assert.False(doc.RootElement.GetProperty("effective").GetBoolean());
        var reason = doc.RootElement.GetProperty("reason").GetString();
        Assert.NotNull(reason);
        Assert.Contains("重建失败", reason);
    }

    [Fact]
    public async Task Response_carries_the_side_effect_note()
    {
        var action = new SetDialogEnhanceAction(_service, new FakeClients(true), Logger);

        var result = await action.HandleAsync(Payload(new { readUrl = true }), CancellationToken.None);

        // 用户开机后直接保存、没走过 getDialogEnhance 时，也要看到这两句
        using var doc = JsonDocument.Parse(Json(result));
        var note = doc.RootElement.GetProperty("note").GetString();
        Assert.NotNull(note);
        Assert.Contains("忽略", note);
        Assert.Contains("拒答", note);
    }

    [Fact]
    public async Task Missing_keys_keep_the_current_value()
    {
        await _service.UpdateAsync(true, false, false, false);
        var action = new SetDialogEnhanceAction(_service, new FakeClients(true), Logger);

        // 只开标题，不该把联网搜索顺手关掉
        await action.HandleAsync(Payload(new { title = true }), CancellationToken.None);

        Assert.True(_service.Current.WebSearch);
        Assert.True(_service.Current.Title);
    }

    [Fact]
    public void Factory_only_swaps_the_transport_when_something_is_enabled()
    {
        Assert.False(ChatClientFactory.NeedsEnhanceTransport(null));
        Assert.False(ChatClientFactory.NeedsEnhanceTransport(new DeepSeekEnhanceOptions()));
        Assert.True(ChatClientFactory.NeedsEnhanceTransport(new DeepSeekEnhanceOptions { Title = true }));
    }

    private static JsonElement Payload(object value) =>
        JsonSerializer.SerializeToElement(value);

    private static string Json(object? value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class FakeClients(bool result) : IChatClientProvider
    {
        public int Reloads { get; private set; }

        public bool HasClient => false;

        public IChatClient Current => throw new InvalidOperationException("测试里不建客户端");

        public string? CurrentModel => null;

        public Task<bool> ReloadAsync(CancellationToken ct = default)
        {
            Reloads++;
            return Task.FromResult(result);
        }
    }
}
