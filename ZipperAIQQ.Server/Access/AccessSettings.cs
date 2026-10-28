using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Access;

/// <summary>
/// 人员与名单的**读写口径**（平台化 + 旧键回退）。
/// </summary>
/// <remarks>
/// <para>
/// 读：**新键存在就用它（空串也算存在）**；只有从未写过才回退旧键（仅 <c>qq</c>）。
/// 回退**不写回**——迁移是惰性的，只要不写就永远读得到，用户也不会因为升级被动改配置。
/// </para>
/// <para>
/// 写：只写新键。旧键保持原样（不删除）——万一回退到旧版程序，行为不会突变。
/// </para>
/// <para>
/// 集中在一处的原因：`getAccess` / `setAccess` / `setOwner` / 启动引导 / 主人放行范围解析
/// 五处都要读同一批键；散着写迟早出现"某处还在读旧键"。
/// </para>
/// </remarks>
public static class AccessSettings
{
    /// <summary>读主人账号。</summary>
    public static Task<string?> GetOwnerAsync(
        IRuntimeSettingsStore settings, string platform, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ReadWithLegacyAsync(settings, AccessKeys.Owner(platform), AccessKeys.LegacyOwnerKey, platform, ct);
    }

    /// <summary>读主人昵称。</summary>
    public static Task<string?> GetOwnerNameAsync(
        IRuntimeSettingsStore settings, string platform, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ReadWithLegacyAsync(
            settings, AccessKeys.OwnerName(platform), AccessKeys.LegacyOwnerNameKey, platform, ct);
    }

    /// <summary>读管理员列表（已拆分、去空白）。</summary>
    public static async Task<IReadOnlyList<string>> GetAdminsAsync(
        IRuntimeSettingsStore settings, string platform, CancellationToken ct = default)
    {
        var (_, values) = await TryGetAdminsAsync(settings, platform, ct).ConfigureAwait(false);
        return values;
    }

    /// <summary>
    /// 读管理员列表，并区分「显式清空」与「从未配置」。
    /// </summary>
    /// <remarks>
    /// 启动引导必须用这个：只看 <c>Count &gt; 0</c> 会把「用户清空」误判成「没配过」，
    /// 于是运行时保留旧名单 —— 重启后管理员/白名单"复活"。
    /// </remarks>
    public static async Task<(bool Found, IReadOnlyList<string> Values)> TryGetAdminsAsync(
        IRuntimeSettingsStore settings, string platform, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var raw = await ReadWithLegacyAsync(
            settings, AccessKeys.Admins(platform), AccessKeys.LegacyAdminsKey, platform, ct).ConfigureAwait(false);

        return (raw is not null, SplitList(raw));
    }

    /// <summary>读主人账号 + 昵称，并区分「显式清空」与「从未配置」。</summary>
    public static async Task<(bool Found, string? OwnerId, string? OwnerName)> TryGetOwnerAsync(
        IRuntimeSettingsStore settings, string platform, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var ownerId = await ReadWithLegacyAsync(
            settings, AccessKeys.Owner(platform), AccessKeys.LegacyOwnerKey, platform, ct).ConfigureAwait(false);
        var ownerName = await ReadWithLegacyAsync(
            settings, AccessKeys.OwnerName(platform), AccessKeys.LegacyOwnerNameKey, platform, ct)
            .ConfigureAwait(false);

        return (ownerId is not null || ownerName is not null, ownerId, ownerName);
    }

    /// <summary>读名单（白 / 黑）。</summary>
    /// <remarks>
    /// 旧模型是「模式 + 单名单」（<c>access.filterMode</c> / <c>access.filterEntries</c>）：
    /// 新键两份都不存在时才回退，并按旧模式折成对应的一份。
    /// </remarks>
    public static async Task<(IReadOnlyList<string> Whitelist, IReadOnlyList<string> Blacklist)>
        GetListsAsync(
            IRuntimeSettingsStore settings, string platform, CancellationToken ct = default)
    {
        var (_, whitelist, blacklist) = await TryGetListsAsync(settings, platform, ct).ConfigureAwait(false);
        return (whitelist, blacklist);
    }

    /// <summary>读名单，并区分「显式清空」与「从未配置」。</summary>
    public static async Task<(bool Found, IReadOnlyList<string> Whitelist, IReadOnlyList<string> Blacklist)>
        TryGetListsAsync(
            IRuntimeSettingsStore settings, string platform, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var rawWhitelist = await ReadWithLegacyAsync(
            settings, AccessKeys.Whitelist(platform), AccessKeys.LegacyWhitelistKey, platform, ct)
            .ConfigureAwait(false);
        var rawBlacklist = await ReadWithLegacyAsync(
            settings, AccessKeys.Blacklist(platform), AccessKeys.LegacyBlacklistKey, platform, ct)
            .ConfigureAwait(false);

        var whitelist = SplitList(rawWhitelist);
        var blacklist = SplitList(rawBlacklist);

        // 新键（或旧版同名键）存在过就认它 —— 显式清空不再被旧「模式 + 单名单」复活
        var found = rawWhitelist is not null || rawBlacklist is not null;

        if (found || whitelist.Count > 0 || blacklist.Count > 0 || !AccessKeys.HasLegacyKeys(platform))
        {
            return (found, whitelist, blacklist);
        }

        // 再往上一层：更旧的「模式 + 单名单」
        var legacyEntries = SplitList(
            await settings.GetAsync(AccessKeys.LegacyFilterEntriesKey, ct).ConfigureAwait(false));
        if (legacyEntries.Count == 0)
        {
            return (found, whitelist, blacklist);
        }

        var legacyMode = await settings.GetAsync(AccessKeys.LegacyFilterModeKey, ct).ConfigureAwait(false);
        return string.Equals(legacyMode, "Whitelist", StringComparison.OrdinalIgnoreCase)
            ? (true, legacyEntries, [])
            : (true, [], legacyEntries);
    }

    /// <summary>把逗号分隔的字符串拆成列表（统一入口，避免各处写法漂移）。</summary>
    public static IReadOnlyList<string> SplitList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<string?> ReadWithLegacyAsync(
        IRuntimeSettingsStore settings,
        string key,
        string legacyKey,
        string platform,
        CancellationToken ct)
    {
        var value = await settings.GetAsync(key, ct).ConfigureAwait(false);
        if (value is not null)
        {
            // 显式写空（清空）也是有效值，不能再回退旧键 —— 否则「清空后重启复活」
            return value;
        }

        return AccessKeys.HasLegacyKeys(platform)
            ? await settings.GetAsync(legacyKey, ct).ConfigureAwait(false)
            : null;
    }
}
