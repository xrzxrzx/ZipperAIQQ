using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Access;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 人员与名单平台化（S3-C 的 C8）：<c>getAccess</c> / <c>setAccess</c> / <c>getOwner</c> / <c>setOwner</c>。
/// </summary>
/// <remarks>
/// <para>
/// 全部用**真实现**（SQLite 设置库 / 配置库、<see cref="ConfiguredRoleResolver"/>、
/// <see cref="ChatFilter"/>）：这一层要验的恰恰是"写库 + 立即生效 + 平台隔离"，
/// 换成假实现就把要验的东西全假掉了。
/// </para>
/// <para>
/// action 返回的是**匿名对象**，所以统一用 <see cref="JsonSerializer.SerializeToElement{TValue}"/>
/// 转成 <see cref="JsonElement"/> 再按属性名断言 —— 不为测试改产品代码，也不依赖内部类型。
/// </para>
/// <para>
/// 数据库用**临时文件**而不是 <c>:memory:</c>：<see cref="SqliteDatabase"/> 每次读写都开新连接。
/// </para>
/// </remarks>
public sealed class AccessActionsTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteRuntimeSettingsStore _settings;
    private readonly SqliteConfigStore _configs;
    private readonly ConfiguredRoleResolver _roles;
    private readonly ChatFilter _filter;
    private readonly ContactDirectoryRegistry _directories;

    public AccessActionsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-access-test-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";

        _db = new SqliteDatabase(_connectionString, Logger);
        _settings = new SqliteRuntimeSettingsStore(_db);
        _configs = new SqliteConfigStore(_db);

        // 启动配置给空的管理员 / 工具白名单：本类只验"库里写的那份"
        _roles = new ConfiguredRoleResolver([], [], AccessKeys.DefaultPlatform);
        _filter = new ChatFilter(new ChatFilterOptions(), AccessKeys.DefaultPlatform);
        _directories = new ContactDirectoryRegistry(
            new Dictionary<string, IContactDirectory> { [AccessKeys.DefaultPlatform] = new FakeContacts() });
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

    // ─────────────────────────── 旧键回退 ───────────────────────────

    [Fact]
    public async Task GetAccess_LegacyKeysOnly_ReadsThemAndEchoesQq()
    {
        // 单平台时代的老配置：只有旧键，没有任何 access.qq.*
        await _settings.SetAsync(AccessKeys.LegacyOwnerKey, "10001");
        await _settings.SetAsync(AccessKeys.LegacyAdminsKey, "42,43");
        await _settings.SetAsync(AccessKeys.LegacyWhitelistKey, "qq:g1");

        var json = await RunAsync(NewGetAccess(), "{}");

        Assert.Equal("qq", json.GetProperty("platform").GetString());
        Assert.Equal("10001", json.GetProperty("ownerId").GetString());
        Assert.Equal(new[] { "42", "43" }, Strings(json.GetProperty("admins")));
        Assert.Equal(new[] { "qq:g1" }, Strings(json.GetProperty("whitelist")));
        Assert.Empty(Strings(json.GetProperty("blacklist")));
    }

    // ─────────────────────────── 写新键 + 立即生效 ───────────────────────────

    [Fact]
    public async Task SetAccess_NewKeys_WritesPlatformKeysAndAppliesImmediately()
    {
        var json = await RunAsync(NewSetAccess(), """
            { "platform": "qq", "admins": ["7"], "whitelist": ["qq:g1"] }
            """);

        Assert.Equal("qq", json.GetProperty("platform").GetString());
        Assert.False(json.GetProperty("skipped").GetBoolean());

        // 写的是新键（带平台段），不是旧键
        Assert.Equal("7", await _settings.GetAsync(AccessKeys.Admins("qq")));
        Assert.Equal("qq:g1", await _settings.GetAsync(AccessKeys.Whitelist("qq")));

        // 立即生效：不用重启，运行中的过滤器就已经变了
        Assert.Equal(1, _filter.WhitelistCount("qq"));
    }

    // ─────────────────────────── 平台隔离 ───────────────────────────

    [Fact]
    public async Task SetAccess_DifferentPlatforms_DoNotPolluteEachOther()
    {
        await RunAsync(NewSetAccess(), """{ "platform": "qq", "whitelist": ["qq:g1"] }""");
        await RunAsync(NewSetAccess(), """{ "platform": "douyin", "blacklist": ["douyin:g7"] }""");

        var qq = await RunAsync(NewGetAccess(), """{ "platform": "qq" }""");
        Assert.Equal(new[] { "qq:g1" }, Strings(qq.GetProperty("whitelist")));
        Assert.Empty(Strings(qq.GetProperty("blacklist")));

        var douyin = await RunAsync(NewGetAccess(), """{ "platform": "douyin" }""");
        Assert.Empty(Strings(douyin.GetProperty("whitelist")));
        Assert.Equal(new[] { "douyin:g7" }, Strings(douyin.GetProperty("blacklist")));
    }

    [Fact]
    public async Task ChatFilter_IsAllowed_IsPerPlatform()
    {
        await RunAsync(NewSetAccess(), """{ "platform": "qq", "whitelist": ["qq:g1"] }""");
        await RunAsync(NewSetAccess(), """{ "platform": "douyin", "blacklist": ["douyin:g7"] }""");

        // QQ 配了白名单 = deny-by-default：不在名单里的群不收
        Assert.False(_filter.IsAllowed(Group("qq", "2")));

        // 抖音没配白名单（只有黑名单）：名单外照收
        Assert.True(_filter.IsAllowed(Group("douyin", "2")));
    }

    // ─────────────────────────── 管理员 × 黑名单冲突（仅同平台）───────────────────────────

    [Fact]
    public async Task SetAccess_AdminConflict_OnlySamePlatform()
    {
        await RunAsync(NewSetAccess(), """{ "platform": "qq", "blacklist": ["qq:p123"] }""");

        // 同一个人在 QQ 被拉黑，不该影响他在抖音的管理员身份（跨平台不冲突）
        await RunAsync(NewSetAccess(), """{ "platform": "douyin", "admins": ["123"] }""");
        Assert.Equal("123", await _settings.GetAsync(AccessKeys.Admins("douyin")));

        // 同平台才冲突：需要 409 + 结构化 payload 让客户端二次确认
        var ex = await Assert.ThrowsAsync<ApiException>(async () =>
            await RunAsync(NewSetAccess(), """{ "platform": "qq", "admins": ["123"] }"""));

        Assert.Equal(ApiResponse.Conflict, ex.Code);
        Assert.Contains("123", ex.Message);
    }

    // ─────────────────────────── 主人：按平台 + 旧键同步 ───────────────────────────

    [Fact]
    public async Task SetOwner_ByPlatform_SyncsLegacyKeyAndTakesEffect()
    {
        await RunAsync(NewSetOwner(), """{ "platform": "qq", "userId": "10001" }""");

        Assert.Equal("10001", await _settings.GetAsync(AccessKeys.Owner("qq")));
        // qq 才有旧键可同步（回退到旧版程序时主人不会凭空消失）
        Assert.Equal("10001", await _settings.GetAsync(AccessKeys.LegacyOwnerKey));

        // 立即生效，且只对 qq 生效
        Assert.Equal(Role.Owner, _roles.Resolve("10001", "qq"));
        Assert.Equal(Role.Normal, _roles.Resolve("10001", "douyin"));
    }

    [Fact]
    public async Task SetOwner_TwoPlatforms_DoNotOverwriteEachOther()
    {
        await RunAsync(NewSetOwner(), """{ "platform": "qq", "userId": "10001" }""");
        await RunAsync(NewSetOwner(), """{ "platform": "douyin", "userId": "dy-9" }""");

        Assert.Equal("10001", await _settings.GetAsync(AccessKeys.Owner("qq")));
        Assert.Equal("dy-9", await _settings.GetAsync(AccessKeys.Owner("douyin")));

        var qq = await RunAsync(NewGetOwner(), """{ "platform": "qq" }""");
        Assert.Equal("10001", qq.GetProperty("ownerId").GetString());

        Assert.Equal(Role.Owner, _roles.Resolve("10001", "qq"));
        Assert.Equal(Role.Owner, _roles.Resolve("dy-9", "douyin"));
    }

    // ─────────────────────────── 显式清空 ≠ 从未配置 ───────────────────────────

    [Fact]
    public async Task Cleared_admins_are_not_revived_by_legacy_key()
    {
        await _settings.SetAsync(AccessKeys.LegacyAdminsKey, "10001", CancellationToken.None);
        await _settings.SetAsync(AccessKeys.Admins("qq"), string.Empty, CancellationToken.None);

        var (found, values) = await AccessSettings.TryGetAdminsAsync(_settings, "qq", CancellationToken.None);

        Assert.True(found);
        Assert.Empty(values);
    }

    [Fact]
    public async Task Absent_new_key_falls_back_to_legacy()
    {
        await _settings.SetAsync(AccessKeys.LegacyAdminsKey, "10001,10002", CancellationToken.None);

        var (found, values) = await AccessSettings.TryGetAdminsAsync(_settings, "qq", CancellationToken.None);

        Assert.True(found);
        Assert.Equal(["10001", "10002"], values);
    }

    [Fact]
    public async Task Never_configured_is_not_found()
    {
        var (found, values) = await AccessSettings.TryGetAdminsAsync(_settings, "douyin", CancellationToken.None);

        Assert.False(found);
        Assert.Empty(values);
    }

    [Fact]
    public async Task Cleared_lists_are_not_revived_by_legacy_filter_mode()
    {
        await _settings.SetAsync(AccessKeys.LegacyFilterEntriesKey, "10001", CancellationToken.None);
        await _settings.SetAsync(AccessKeys.LegacyFilterModeKey, "Whitelist", CancellationToken.None);
        await _settings.SetAsync(AccessKeys.Whitelist("qq"), string.Empty, CancellationToken.None);
        await _settings.SetAsync(AccessKeys.Blacklist("qq"), string.Empty, CancellationToken.None);

        var (found, whitelist, blacklist) =
            await AccessSettings.TryGetListsAsync(_settings, "qq", CancellationToken.None);

        Assert.True(found);
        Assert.Empty(whitelist);
        Assert.Empty(blacklist);
    }

    // ─────────────────────────── 帮手 ───────────────────────────

    private GetAccessAction NewGetAccess() => new(_settings, _roles, _filter, _directories);

    private SetAccessAction NewSetAccess() => new(_settings, _roles, _filter, _configs);

    private GetOwnerAction NewGetOwner() => new(_settings, _directories);

    private SetOwnerAction NewSetOwner() => new(_settings, _roles, new FakeOwnerScope(), _configs);

    private static async Task<JsonElement> RunAsync(IApiAction action, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var data = await action.HandleAsync(doc.RootElement, CancellationToken.None);
        return JsonSerializer.SerializeToElement(data, WebJson);
    }

    private static string[] Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static ChatRef Group(string platform, string groupId) =>
        new() { Platform = platform, Type = ChatType.Group, Id = groupId };

    /// <summary>固定返回一个好友 / 一个群的联系人目录（不连真实 NapCat）。</summary>
    private sealed class FakeContacts : IContactDirectory
    {
        public Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContactInfo>>([new ContactInfo("10001", "主人", null)]);

        public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GroupInfo>>([new GroupInfo("1", "测试群", 2, 200)]);

        public Task SetSignatureAsync(string text, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendPokeAsync(string userId, string? groupId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<GroupInfo?> GetGroupAsync(string groupId, CancellationToken ct = default) =>
            Task.FromResult<GroupInfo?>(null);

        public Task<IReadOnlyList<GroupMemberInfo>> GetGroupMembersAsync(
            string groupId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GroupMemberInfo>>([]);

        public Task<JsonNode?> InvokeApiAsync(
            string action, JsonObject? parameters, CancellationToken ct = default) =>
            Task.FromResult<JsonNode?>(null);
    }

    /// <summary>主人放行范围：<c>setOwner</c> 只负责触发后台刷新，测试里不需要真去解析群。</summary>
    private sealed class FakeOwnerScope : IOwnerScope
    {
        public IReadOnlyCollection<string> Current => [];

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
