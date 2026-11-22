using System.Text.Json;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Server.Ai;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Search;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 密钥收敛（问题记录 06 §5）的**接线级**用例：真实 SQLite + 假密钥环，覆盖
/// 「旧路写入也落到槽位」「视图只回掩码」「搜索状态不用重启就正确」「legacy 回落」四条。
/// </summary>
/// <remarks>
/// 这里刻意不用 mock provider 而用真 <see cref="DeepSeekKeyProvider"/>：
/// 上一轮审查抓到的 P0 就是「有一条路径绕过了 provider」，只测 provider 内部根本发现不了。
/// </remarks>
public sealed class KeyConvergenceTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteConfigStore _configs;
    private readonly SqliteApiKeyStore _keyStore;
    private readonly SqliteRuntimeSettingsStore _settings;
    private readonly DeepSeekKeyProvider _keys;
    private readonly FakeChatClients _clients;
    private readonly RuntimeConfigService _runtime;

    public KeyConvergenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-keyconv-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _configs = new SqliteConfigStore(_db);
        _keyStore = new SqliteApiKeyStore(_db);
        _settings = new SqliteRuntimeSettingsStore(_db);
        _keys = new DeepSeekKeyProvider(_keyStore, _configs, new FakeProtector(), Logger);
        _clients = new FakeChatClients();
        _runtime = new RuntimeConfigService(_configs, new FakeProtector(), _clients, _keys, Logger);
    }

    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();
        await _keys.InitializeAsync();
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
    public async Task SetConfig_with_a_primary_key_also_writes_the_global_slot()
    {
        // 旧客户端 / 旧脚本仍走这条路：只写端点密文的话，界面上「已保存」而实际用的是旧 key
        var changed = await _runtime.SetEndpointAsync(
            new EndpointInput("primary", "https://api.deepseek.com", "deepseek-flash", "Chat", "sk-legacy-path-1234"),
            "tester");

        Assert.True(changed);
        Assert.Equal("sk-legacy-path-1234", await _keys.GetAsync());

        // 端点表里的密文也照旧写（留档口径不变），但**不是**运行时读的地方
        var endpoint = await _configs.GetEndpointAsync("primary");
        Assert.NotNull(endpoint?.KeyCipher);

        Assert.True(_clients.Reloaded);
    }

    [Fact]
    public async Task Config_view_returns_mask_and_never_the_plaintext()
    {
        const string plain = "sk-abcdefghijklmnopqrstuvwxyz012345";
        await _runtime.SetApiKeyAsync(plain, "tester");

        var view = await _runtime.GetViewAsync();
        var json = JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain(plain, json, StringComparison.Ordinal);
        Assert.Contains("sk-****2345", json, StringComparison.Ordinal);
        Assert.Contains("\"slots\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Short_key_is_rejected_before_it_reaches_the_database()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(
            () => _runtime.SetApiKeyAsync("sk-1", "tester"));

        Assert.Equal(ApiResponse.BadRequest, ex.Code);
        Assert.False(_keys.HasKey);
    }

    [Fact]
    public async Task Search_status_flips_to_configured_without_a_restart()
    {
        var search = NewSearch();

        await search.InitializeAsync();
        Assert.False(search.Current.HasKey);

        await _runtime.SetApiKeyAsync("sk-abcdefghijklmnop", "tester");

        // getSearch 读的就是 Current：不快照刷新就会出现「功能能用、界面说缺 key」
        Assert.True(search.Current.HasKey);
        Assert.Equal("sk-abcdefghijklmnop", await search.GetKeyAsync());
    }

    [Fact]
    public async Task Search_falls_back_to_the_legacy_endpoint_cipher_when_no_global_key_exists()
    {
        await _configs.UpsertEndpointAsync(new ModelEndpointSetting(
            "search",
            "https://api.deepseek.com",
            "search",
            ApiKind: 0,
            KeyCipher: new FakeProtector().Protect("sk-legacy-search-key"),
            UpdatedAt: DateTimeOffset.UtcNow));

        var search = NewSearch();
        await search.InitializeAsync();

        Assert.True(search.Current.HasKey);
        Assert.Equal("sk-legacy-search-key", await search.GetKeyAsync());
    }

    [Fact]
    public async Task SetApiKey_action_response_never_contains_the_plaintext()
    {
        // 「回显」的最终出口是 action 返回给客户端的那份 JSON，不是 GetViewAsync 的匿名对象
        const string plain = "sk-response-check-0123456789abcdef";
        var action = new SetApiKeyAction(_runtime);
        var payload = JsonSerializer.SerializeToElement(new { key = plain });

        var result = await action.HandleAsync(payload, CancellationToken.None);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain(plain, json, StringComparison.Ordinal);
        Assert.Contains("sk-****cdef", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_from_the_judge_endpoint_shows_up_as_masked_in_the_view()
    {
        // 「升级后无需重填」的另一半：迁移来源是 judge 时，视图里也得是掩码而不是明文
        await _configs.UpsertEndpointAsync(new ModelEndpointSetting(
            "judge",
            "https://api.example",
            "model",
            ApiKind: 0,
            KeyCipher: new FakeProtector().Protect("sk-from-judge-abcdefgh"),
            UpdatedAt: DateTimeOffset.UtcNow));

        await _keys.InitializeAsync();

        var view = await _runtime.GetViewAsync();
        var json = JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("sk-****efgh", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-from-judge-abcdefgh", json, StringComparison.Ordinal);
    }

    private SearchOptionsService NewSearch() => new(
        _settings,
        _configs,
        new FakeProtector(),
        _keys,
        new ServerOptions
        {
            NapCatWebSocketUrl = "ws://127.0.0.1:1",
            LlmApiKey = "unused",
            LlmBaseUrl = "https://api.deepseek.com",
            LlmModel = "deepseek-flash",
            SearchEnabled = true,
        },
        Logger);

    /// <summary>假加密：<c>enc:</c> 前缀代表合法密文（与 provider 单测同口径）。</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        private const string Prefix = "enc:";

        public string Protect(string plaintext) => Prefix + plaintext;

        public string? Unprotect(string? cipher) =>
            cipher is not null && cipher.StartsWith(Prefix, StringComparison.Ordinal)
                ? cipher[Prefix.Length..]
                : null;
    }

    private sealed class FakeChatClients : IChatClientProvider
    {
        public bool Reloaded { get; private set; }

        public bool HasClient => false;

        public IChatClient Current => throw new InvalidOperationException("测试里不建客户端");

        public string? CurrentModel => null;

        public Task ReloadAsync(CancellationToken ct = default)
        {
            Reloaded = true;
            return Task.CompletedTask;
        }
    }
}
