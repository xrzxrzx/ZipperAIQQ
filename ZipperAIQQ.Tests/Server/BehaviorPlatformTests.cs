using System.Text.Json;
using Serilog;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 通知口径按平台（S3-D 的 D2 / 决策六）：<c>getBehavior</c> / <c>setBehavior</c> 的平台键。
/// </summary>
/// <remarks>
/// <para>
/// 用**真实现**（临时文件 SQLite + <see cref="DatabaseInitializer"/> + <see cref="SqliteRuntimeSettingsStore"/>）：
/// 这一层要验的恰恰是"键真的写成了什么样、旧键还能不能读"，换成假的 KV 就把要验的东西假掉了。
/// </para>
/// <para>
/// 键名在断言里写**字面量**（<c>notice.reply:qq:Poke</c>）而不是调用 action 的键拼法工具：
/// 这个测试同时是**键形态的契约**——用被测代码生成期望值，就变成"自己验自己"了。
/// </para>
/// <para>
/// 数据库用**临时文件**而不是 <c>:memory:</c>：<see cref="SqliteDatabase"/> 每次读写都开新连接。
/// </para>
/// </remarks>
public sealed class BehaviorPlatformTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteRuntimeSettingsStore _settings;
    private readonly SqliteConfigStore _configs;

    public BehaviorPlatformTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-behavior-test-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";

        _db = new SqliteDatabase(_connectionString, Logger);
        _settings = new SqliteRuntimeSettingsStore(_db);
        _configs = new SqliteConfigStore(_db);
    }

    public async Task InitializeAsync()
    {
        var initializer = new DatabaseInitializer(_connectionString, Logger);
        await initializer.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        // 连接池可能还持有句柄，忽略删除失败
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    // ─────────────────────────── 默认值：与改动前完全一致 ───────────────────────────

    [Fact]
    public async Task GetBehavior_DefaultsUnchanged_OnFreshDatabase()
    {
        // 现状（S3-D 之前）：没有任何地方给 notice.reply:* 播种，键不存在 ⇒ 一律"关"。
        // 平台化之后这条**不许变**——顺手改默认值等于替用户把通知打开，群里会吵。
        var json = await RunAsync(NewGetBehavior(), "{}");
        var notices = json.GetProperty("notices").EnumerateArray().ToList();

        Assert.Equal(GetBehaviorAction.ReplyableNotices.Length, notices.Count);
        Assert.All(notices, item => Assert.False(item.GetProperty("enabled").GetBoolean()));

        // 回复模式的老默认值也不许变
        Assert.Equal("every", json.GetProperty("replyMode").GetString());
    }

    // ─────────────────────────── 读：每项带 platform + 顶层 platforms ───────────────────────────

    [Fact]
    public async Task GetBehavior_NoticesCarryPlatformAndPlatforms()
    {
        var json = await RunAsync(NewGetBehavior(), """{ "platform": "qq" }""");

        Assert.Equal("qq", json.GetProperty("platform").GetString());

        // 现有 21 个通知类型全归 QQ（NoticeCatalog.PlatformOf），所以 platforms 现在只有 qq
        Assert.Contains("qq", Strings(json.GetProperty("platforms")));

        var notices = json.GetProperty("notices").EnumerateArray().ToList();
        Assert.NotEmpty(notices);
        Assert.All(notices, item => Assert.Equal("qq", item.GetProperty("platform").GetString()));

        // 既有字段一个都不能少、不能改名（老客户端还在按这些名字读）
        var first = notices[0];
        Assert.Equal("Poke", first.GetProperty("type").GetString());
        Assert.Equal("戳一戳", first.GetProperty("label").GetString());
        Assert.Equal("common", first.GetProperty("group").GetString());
        Assert.Equal("常用", first.GetProperty("groupLabel").GetString());
        Assert.True(first.TryGetProperty("enabled", out _));
        Assert.True(json.TryGetProperty("replyMode", out _));
        Assert.True(json.TryGetProperty("replyModes", out _));

        // 查询平台只是回显（缺省 qq；显式给 douyin 也照样回显）
        var douyin = await RunAsync(NewGetBehavior(), """{ "platform": "douyin" }""");
        Assert.Equal("douyin", douyin.GetProperty("platform").GetString());
    }

    // ─────────────────────────── 写：新键 + qq 镜像旧键 ───────────────────────────

    [Fact]
    public async Task SetBehavior_WritesPlatformKey()
    {
        var json = await RunAsync(NewSetBehavior(), """{ "platform": "qq", "notice": { "Poke": true } }""");

        Assert.Equal("qq", json.GetProperty("platform").GetString());

        // 新键（带平台段）
        Assert.Equal("1", await _settings.GetAsync("notice.reply:qq:Poke"));

        // 旧键也被写了一份：回退到旧版程序时开关不会突然变空
        Assert.Equal("1", await _settings.GetAsync("notice.reply:Poke"));

        // 读回来是开的
        var behavior = await RunAsync(NewGetBehavior(), """{ "platform": "qq" }""");
        Assert.True(FindNotice(behavior, "Poke").GetProperty("enabled").GetBoolean());
    }

    // ─────────────────────────── 读：老配置（只有旧键）仍然有效 ───────────────────────────

    [Fact]
    public async Task SetBehavior_LegacyKeyStillRead()
    {
        // 单平台时代的老配置：只有旧键，没有任何 notice.reply:qq:*
        await _settings.SetAsync("notice.reply:Poke", "1");

        var json = await RunAsync(NewGetBehavior(), "{}");
        Assert.True(FindNotice(json, "Poke").GetProperty("enabled").GetBoolean());

        // 回退**不写回**（惰性迁移）：读一次不该偷偷造出新键
        Assert.Null(await _settings.GetAsync("notice.reply:qq:Poke"));
    }

    // ─────────────────────────── 校验：未知平台直接 400 ───────────────────────────

    [Fact]
    public async Task SetBehavior_UnknownPlatform_ReturnsBadRequest()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(async () =>
            await RunAsync(NewSetBehavior(), """{ "platform": "telegram", "notice": { "Poke": true } }"""));

        Assert.Equal(ApiResponse.BadRequest, ex.Code);
        Assert.Contains("telegram", ex.Message);

        // 一个键都没写：绝不静默落到错的平台 / 旧键上
        Assert.Null(await _settings.GetAsync("notice.reply:qq:Poke"));
        Assert.Null(await _settings.GetAsync("notice.reply:Poke"));
    }

    // ─────────────────────────── 平台隔离：各读各的 ───────────────────────────

    [Fact]
    public async Task SetBehavior_DouyinKeysAreSeparate()
    {
        await RunAsync(NewSetBehavior(), """{ "platform": "douyin", "notice": { "Poke": true } }""");

        Assert.Equal("1", await _settings.GetAsync("notice.reply:douyin:Poke"));
        // 没碰 qq 的键，也没镜像老键（老键是 qq 专属）
        Assert.Null(await _settings.GetAsync("notice.reply:qq:Poke"));
        Assert.Null(await _settings.GetAsync("notice.reply:Poke"));

        // 反过来改 qq，也不影响抖音
        await RunAsync(NewSetBehavior(), """{ "platform": "qq", "notice": { "Poke": false } }""");

        Assert.Equal("0", await _settings.GetAsync("notice.reply:qq:Poke"));
        Assert.Equal("1", await _settings.GetAsync("notice.reply:douyin:Poke"));
    }

    // ─────────────────────────── 单项 platform 优先于顶层 ───────────────────────────

    [Fact]
    public async Task SetBehavior_PerItemPlatformOverridesTopLevel()
    {
        await RunAsync(NewSetBehavior(), """
            {
              "platform": "qq",
              "notices": [
                { "type": "GroupBan", "enabled": true, "platform": "douyin" },
                { "type": "Poke", "enabled": true }
              ]
            }
            """);

        // 自带 platform 的按单项写
        Assert.Equal("1", await _settings.GetAsync("notice.reply:douyin:GroupBan"));
        Assert.Null(await _settings.GetAsync("notice.reply:qq:GroupBan"));

        // 没带的按顶层 platform（qq）写，且镜像旧键
        Assert.Equal("1", await _settings.GetAsync("notice.reply:qq:Poke"));
        Assert.Equal("1", await _settings.GetAsync("notice.reply:Poke"));
    }

    // ─────────────────────────── 帮手 ───────────────────────────

    private GetBehaviorAction NewGetBehavior() => new(_settings);

    private SetBehaviorAction NewSetBehavior() => new(_settings, _configs);

    private static async Task<JsonElement> RunAsync(IApiAction action, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var data = await action.HandleAsync(doc.RootElement, CancellationToken.None);
        return JsonSerializer.SerializeToElement(data, WebJson);
    }

    /// <summary>按类型名找 notices 里的那一项。</summary>
    private static JsonElement FindNotice(JsonElement behavior, string type) =>
        behavior.GetProperty("notices")
            .EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == type);

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToArray();
}
