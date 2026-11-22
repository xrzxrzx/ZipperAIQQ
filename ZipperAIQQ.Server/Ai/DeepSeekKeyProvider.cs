using Serilog;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// <see cref="IDeepSeekKeyProvider"/> 的实现：槽位表 + legacy 端点密文迁移（方案 G7）。
/// </summary>
/// <remarks>
/// <para>
/// <b>迁移顺序</b>：读槽位表 → 空则依次尝试 <c>primary</c> / <c>judge</c> / <c>search</c>
/// 三个端点的旧密文 → **原样搬进槽位 0**（密文不重新加密，密钥环不变就一定能解开）→
/// 端点表那三行**不删不改**。同一套搬运还负责「槽位表有行但一把都解不开」的修复（见 <see cref="InitializeAsync"/>）。
/// </para>
/// <para>
/// <b>为什么搬密文而不是搬明文</b>：搬明文意味着中间态里多一次「解密 → 明文进内存 → 再次加密」，
/// 多一步就多一次写坏的机会；密文直接搬，失败时旧库仍然完好。
/// </para>
/// <para>
/// <b>为什么缓存</b>：<see cref="GetAsync"/> 会被每轮对话、每次搜索、每次媒体调用命中，
/// 每次都开库解密会让启动后第一条消息慢一截。缓存只在 Initialize / Set / Reload 时刷新。
/// </para>
/// </remarks>
public sealed class DeepSeekKeyProvider : IDeepSeekKeyProvider
{
    /// <summary>生效槽位（对外那一把）。</summary>
    public const int PrimarySlot = 0;

    /// <summary>legacy 迁移的取值顺序：先看主端点，再看判断端点，最后看搜索端点。</summary>
    private static readonly string[] LegacyEndpointOrder = ["primary", "judge", "search"];

    private readonly IApiKeyStore _store;
    private readonly IConfigStore _configs;
    private readonly ISecretProtector _protector;
    private readonly ILogger _logger;

    /// <summary>已解密的快照；**不可变**，靠整体替换保证读写不撕裂。</summary>
    private volatile KeySnapshot _snapshot = KeySnapshot.Empty;

    public DeepSeekKeyProvider(
        IApiKeyStore store,
        IConfigStore configs,
        ISecretProtector protector,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _configs = configs;
        _protector = protector;
        _logger = logger;
    }

    public bool HasKey => !string.IsNullOrEmpty(_snapshot.Primary);

    public Task<string?> GetAsync(CancellationToken ct = default) => Task.FromResult(_snapshot.Primary);

    public DeepSeekKeyStatus GetStatus()
    {
        var primary = _snapshot.Primary;
        return new DeepSeekKeyStatus(
            Configured: !string.IsNullOrEmpty(primary),
            Slots: _snapshot.All.Count,
            Masked: Mask(primary));
    }

    /// <summary>掩码：<c>sk-****a1b2</c>；少于 8 位整体打码（与端点 key 的展示口径一致）。</summary>
    private static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? string.Empty
        : key.Length <= 8 ? "****"
        : $"{key[..3]}****{key[^4..]}";

    /// <summary>
    /// 启动时调用：**先迁移再加载**。迁移失败不阻断启动（key 缺失只是相关能力不可用）。
    /// </summary>
    /// <remarks>
    /// 两条路径都走迁移：① 槽位表为空（首次升级）；② 槽位表有行但一把都解不开
    /// （密钥环被换过 / 密文损坏）—— 这时 legacy 端点密文是唯一的修复来源，不去试就只能一直「无 key」。
    /// </remarks>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var stored = await _store.ListAsync(ct).ConfigureAwait(false);
            if (stored.Count == 0)
            {
                await MigrateLegacyAsync(ct).ConfigureAwait(false);
                stored = await _store.ListAsync(ct).ConfigureAwait(false);
            }

            Apply(stored);

            if (!HasKey && stored.Count > 0)
            {
                _logger.Warning(
                    "槽位表有 {Count} 行但一把都解不开（密钥环被换过？），尝试用 legacy 端点密文修复",
                    stored.Count);

                if (await MigrateLegacyAsync(ct).ConfigureAwait(false))
                {
                    Apply(await _store.ListAsync(ct).ConfigureAwait(false));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读库失败不能让服务起不来：没有 key 时对话客户端保留旧实例（这是既有语义）
            _logger.Error(ex, "读取 API key 槽位失败，本轮不做密钥收敛（沿用 legacy 端点密文）");
        }
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var stored = await _store.ListAsync(ct).ConfigureAwait(false);
        Apply(stored);
    }

    /// <summary>
    /// 写入生效 key（只动槽位 0，**其余槽位原样保留**）。
    /// </summary>
    /// <returns>是否真的发生了变更（密文相同则跳过写库与审计）。</returns>
    public async Task<bool> SetKeyAsync(string plainKey, string actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var trimmed = plainKey.Trim();
        if (string.Equals(trimmed, _snapshot.Primary, StringComparison.Ordinal))
        {
            return false;
        }

        await _store.UpsertAsync(PrimarySlot, _protector.Protect(trimmed), ct).ConfigureAwait(false);
        await _configs.AppendAuditAsync("setApiKey", actor, $"slot{PrimarySlot}", ct).ConfigureAwait(false);

        await ReloadAsync(ct).ConfigureAwait(false);

        _logger.Information(
            "DeepSeek key 已更新（槽位 {Slot}）：留档 {Slots} 把，生效的是最前面那把",
            PrimarySlot, _snapshot.All.Count);
        return true;
    }

    /// <summary>
    /// 把 legacy 端点密文搬进槽位 0（写的是**同一份密文**，不重新加密）。
    /// </summary>
    /// <returns>是否搬成功。</returns>
    private async Task<bool> MigrateLegacyAsync(CancellationToken ct)
    {
        foreach (var name in LegacyEndpointOrder)
        {
            var endpoint = await _configs.GetEndpointAsync(name, ct).ConfigureAwait(false);
            if (endpoint?.KeyCipher is null)
            {
                continue;
            }

            if (_protector.Unprotect(endpoint.KeyCipher) is null)
            {
                _logger.Warning(
                    "端点 {Name} 的 key 解不开（密钥环被换过？），跳过迁移", name);
                continue;
            }

            await _store.UpsertAsync(PrimarySlot, endpoint.KeyCipher, ct).ConfigureAwait(false);
            await _configs
                .AppendAuditAsync("migrateApiKey", "startup", $"{name}->slot{PrimarySlot}", ct)
                .ConfigureAwait(false);

            _logger.Information(
                "已把端点 {Name} 的 key 提升为全局生效 key（槽位 {Slot}）；端点表里的旧密文保留不删",
                name, PrimarySlot);
            return true;
        }

        return false;
    }

    /// <summary>解密整个槽位表并整体替换快照。</summary>
    private void Apply(IReadOnlyList<StoredApiKey> stored)
    {
        var keys = new List<string>(stored.Count);
        foreach (var row in stored)
        {
            var plain = _protector.Unprotect(row.KeyCipher);
            if (string.IsNullOrEmpty(plain))
            {
                _logger.Warning("槽位 {Slot} 的 key 解不开，已跳过（不删库，便于人工恢复）", row.Slot);
                continue;
            }

            keys.Add(plain);
        }

        _snapshot = new KeySnapshot(keys.Count > 0 ? keys[0] : null, keys);

        if (_snapshot.Primary is null)
        {
            _logger.Warning(
                "没有任何可用的 API key：对话 / 搜索 / OCR / TTS 会各自按「未配置」降级，请在客户端填一把 DeepSeek Key");
        }
    }

    /// <summary>
    /// 不可变快照：<see cref="Primary"/> 是生效那把（槽位最小的可解开项），<see cref="All"/> 是全部。
    /// </summary>
    private sealed record KeySnapshot(string? Primary, IReadOnlyList<string> All)
    {
        public static readonly KeySnapshot Empty = new(null, []);
    }
}
