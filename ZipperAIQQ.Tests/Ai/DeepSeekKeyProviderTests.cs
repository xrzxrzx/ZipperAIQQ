using Serilog;
using ZipperAIQQ.Server.Ai;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Tests.Ai;

/// <summary>
/// 全局 key provider（问题记录 06 §5）：legacy 迁移的三条分支、槽位 0 的语义、
/// 以及「只动槽位 0、其余留档」这条底线。
/// </summary>
public sealed class DeepSeekKeyProviderTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task Migrates_primary_endpoint_cipher_into_slot_zero()
    {
        var store = new FakeApiKeyStore();
        var configs = new FakeConfigStore();
        configs.Endpoints["primary"] = Endpoint("primary", "enc:sk-primary");

        var provider = NewProvider(store, configs);
        await provider.InitializeAsync();

        Assert.True(provider.HasKey);
        Assert.Equal("sk-primary", await provider.GetAsync());

        var stored = await store.GetAsync(DeepSeekKeyProvider.PrimarySlot);
        Assert.NotNull(stored);
        // 搬的是**密文**：provider 不解密再加密，中间态里不出现明文
        Assert.Equal("enc:sk-primary", stored!.KeyCipher);
        Assert.Contains("migrateApiKey", configs.Audits.Select(a => a.Action));
    }

    [Fact]
    public async Task Falls_back_to_judge_when_primary_has_no_key()
    {
        var store = new FakeApiKeyStore();
        var configs = new FakeConfigStore();
        configs.Endpoints["primary"] = Endpoint("primary", null);
        configs.Endpoints["judge"] = Endpoint("judge", "enc:sk-judge");

        var provider = NewProvider(store, configs);
        await provider.InitializeAsync();

        Assert.Equal("sk-judge", await provider.GetAsync());
        Assert.Contains(configs.Audits, a => a.Target == "judge->slot0");
    }

    [Fact]
    public async Task Falls_back_to_search_when_others_are_empty()
    {
        var store = new FakeApiKeyStore();
        var configs = new FakeConfigStore();
        configs.Endpoints["search"] = Endpoint("search", "enc:sk-search");

        var provider = NewProvider(store, configs);
        await provider.InitializeAsync();

        Assert.Equal("sk-search", await provider.GetAsync());
    }

    [Fact]
    public async Task Migration_is_idempotent_and_does_not_overwrite_existing_slot_zero()
    {
        var store = new FakeApiKeyStore();
        await store.UpsertAsync(0, "enc:sk-global");

        var configs = new FakeConfigStore();
        configs.Endpoints["primary"] = Endpoint("primary", "enc:sk-legacy");

        var provider = NewProvider(store, configs);
        await provider.InitializeAsync();

        Assert.Equal("sk-global", await provider.GetAsync());
        Assert.Empty(configs.Audits);
    }

    [Fact]
    public async Task Legacy_ciphers_are_left_in_the_endpoint_table()
    {
        // 「存量密文不删」是需求硬要求：迁移只往槽位表**增加**，不动端点表
        var store = new FakeApiKeyStore();
        var configs = new FakeConfigStore();
        configs.Endpoints["primary"] = Endpoint("primary", "enc:sk-legacy");

        await NewProvider(store, configs).InitializeAsync();

        Assert.Equal("enc:sk-legacy", configs.Endpoints["primary"].KeyCipher);
        Assert.Single(configs.Endpoints);
    }

    [Fact]
    public async Task Unreadable_cipher_is_skipped_and_migration_moves_on()
    {
        var store = new FakeApiKeyStore();
        var configs = new FakeConfigStore();
        configs.Endpoints["primary"] = Endpoint("primary", "GARBAGE");
        configs.Endpoints["judge"] = Endpoint("judge", "enc:sk-judge");

        var provider = NewProvider(store, configs);
        await provider.InitializeAsync();

        Assert.Equal("sk-judge", await provider.GetAsync());
    }

    [Fact]
    public async Task Set_key_only_touches_slot_zero()
    {
        var store = new FakeApiKeyStore();
        await store.UpsertAsync(0, "enc:old");
        await store.UpsertAsync(1, "enc:archived");

        var configs = new FakeConfigStore();
        var provider = NewProvider(store, configs);
        await provider.InitializeAsync();

        var changed = await provider.SetKeyAsync("sk-new", "tester");

        Assert.True(changed);
        Assert.Equal("sk-new", await provider.GetAsync());

        var all = await store.ListAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("enc:archived", all[1].KeyCipher);
        Assert.Contains(configs.Audits, a => a.Action == "setApiKey");
    }

    [Fact]
    public async Task Setting_the_same_key_is_a_noop()
    {
        var store = new FakeApiKeyStore();
        await store.UpsertAsync(0, "enc:sk-same");

        var configs = new FakeConfigStore();
        var provider = NewProvider(store, configs);
        await provider.InitializeAsync();

        Assert.False(await provider.SetKeyAsync("sk-same", "tester"));
        Assert.Empty(configs.Audits);
    }

    [Fact]
    public async Task Status_reports_slot_count_and_masked_key_only()
    {
        var store = new FakeApiKeyStore();
        await store.UpsertAsync(0, "enc:sk-first1234567890abcd");
        await store.UpsertAsync(1, "enc:sk-second");

        var provider = NewProvider(store, new FakeConfigStore());
        await provider.InitializeAsync();

        var status = provider.GetStatus();

        Assert.True(status.Configured);
        Assert.Equal(2, status.Slots);
        // 掩码口径：前 3 + **** + 后 4；且**不含明文主体**
        Assert.Equal("sk-****abcd", status.Masked);
        Assert.DoesNotContain("first1234567890", status.Masked);
        Assert.Equal("sk-first1234567890abcd", await provider.GetAsync());
    }

    [Fact]
    public async Task No_key_anywhere_reports_unconfigured_instead_of_throwing()
    {
        var provider = NewProvider(new FakeApiKeyStore(), new FakeConfigStore());
        await provider.InitializeAsync();

        Assert.False(provider.HasKey);
        Assert.Null(await provider.GetAsync());

        var status = provider.GetStatus();
        Assert.False(status.Configured);
        Assert.Equal(0, status.Slots);
        Assert.Equal(string.Empty, status.Masked);
    }

    [Fact]
    public async Task Undecryptable_slots_are_repaired_from_legacy_endpoint_cipher()
    {
        // 密钥环被换过：槽位表有行但一把都解不开。这时必须去试 legacy 端点密文，否则永远「无 key」
        var store = new FakeApiKeyStore();
        await store.UpsertAsync(0, "BROKEN-SLOT");

        var configs = new FakeConfigStore();
        configs.Endpoints["primary"] = Endpoint("primary", "enc:sk-repaired");

        var provider = NewProvider(store, configs);
        await provider.InitializeAsync();

        Assert.True(provider.HasKey);
        Assert.Equal("sk-repaired", await provider.GetAsync());
        Assert.Equal("enc:sk-repaired", (await store.GetAsync(0))!.KeyCipher);
        Assert.Contains(configs.Audits, a => a.Target == "primary->slot0");
    }

    [Fact]
    public async Task Reload_picks_up_keys_written_behind_its_back()
    {
        var store = new FakeApiKeyStore();
        var provider = NewProvider(store, new FakeConfigStore());
        await provider.InitializeAsync();
        Assert.False(provider.HasKey);

        await store.UpsertAsync(0, "enc:sk-outside");
        await provider.ReloadAsync();

        Assert.Equal("sk-outside", await provider.GetAsync());
    }

    private static DeepSeekKeyProvider NewProvider(FakeApiKeyStore store, FakeConfigStore configs) =>
        new(store, configs, new FakeProtector(), Logger);

    private static ModelEndpointSetting Endpoint(string name, string? cipher) =>
        new(name, "https://api.deepseek.com", "deepseek-flash", 0, cipher, DateTimeOffset.UnixEpoch);

    /// <summary>假加密：<c>enc:</c> 前缀代表合法密文，别的都当解不开。</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        private const string Prefix = "enc:";

        public string Protect(string plaintext) => Prefix + plaintext;

        public string? Unprotect(string? cipher) =>
            cipher is not null && cipher.StartsWith(Prefix, StringComparison.Ordinal)
                ? cipher[Prefix.Length..]
                : null;
    }

    private sealed class FakeApiKeyStore : IApiKeyStore
    {
        private readonly Dictionary<int, StoredApiKey> _rows = [];

        public Task<IReadOnlyList<StoredApiKey>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredApiKey>>(
                _rows.Values.OrderBy(r => r.Slot).ToList());

        public Task<StoredApiKey?> GetAsync(int slot, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(slot, out var row) ? row : null);

        public Task UpsertAsync(int slot, string keyCipher, CancellationToken ct = default)
        {
            _rows[slot] = new StoredApiKey(slot, keyCipher, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int slot, CancellationToken ct = default)
        {
            _rows.Remove(slot);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConfigStore : IConfigStore
    {
        public Dictionary<string, ModelEndpointSetting> Endpoints { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<AuditEntry> Audits { get; } = [];

        public Task<IReadOnlyList<ModelEndpointSetting>> ListEndpointsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModelEndpointSetting>>(Endpoints.Values.ToList());

        public Task<ModelEndpointSetting?> GetEndpointAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(Endpoints.TryGetValue(name, out var e) ? e : null);

        public Task UpsertEndpointAsync(ModelEndpointSetting endpoint, CancellationToken ct = default)
        {
            Endpoints[endpoint.Name] = endpoint;
            return Task.CompletedTask;
        }

        public Task DeleteEndpointAsync(string name, CancellationToken ct = default)
        {
            Endpoints.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RoleBinding>> ListRolesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RoleBinding>>([]);

        public Task SetRoleAsync(string role, string endpointName, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task AppendAuditAsync(
            string action, string actor, string? target, CancellationToken ct = default)
        {
            Audits.Add(new AuditEntry(Audits.Count + 1, action, actor, target, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>(Audits);
    }
}
