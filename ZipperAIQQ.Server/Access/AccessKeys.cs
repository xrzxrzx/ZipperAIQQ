namespace ZipperAIQQ.Server.Access;

/// <summary>
/// 人员与名单的**存储键**（S3-C 平台化）。
/// </summary>
/// <remarks>
/// <para>
/// 新键一律带平台：<c>access.{platform}.{owner|admins|whitelist|blacklist}</c>。
/// 旧键（单平台时代，无平台段）**只读不写**：读不到新键时回落到旧键（等价于 <c>qq</c>），
/// 这样老配置不需要迁移动作，也不会因为一次升级丢掉主人 / 管理员 / 名单。
/// </para>
/// <para>
/// 为什么不用「旧键 + 平台后缀」而是「新前缀」：<c>owner.userId</c> 这种旧键本身没有平台语义，
/// 强行拼后缀会造出 <c>owner.userId.douyin</c> 这种歧义键；统一成 <c>access.*</c> 更容易一眼看出来。
/// </para>
/// </remarks>
public static class AccessKeys
{
    /// <summary>默认平台（缺省参数、旧配置的归属）。</summary>
    public const string DefaultPlatform = "qq";

    // ── 旧键：只读兼容（写永远写新键）──
    public const string LegacyOwnerKey = "owner.userId";
    public const string LegacyOwnerNameKey = "owner.nickname";
    public const string LegacyAdminsKey = "access.admins";
    public const string LegacyWhitelistKey = "access.whitelist";
    public const string LegacyBlacklistKey = "access.blacklist";
    public const string LegacyFilterModeKey = "access.filterMode";
    public const string LegacyFilterEntriesKey = "access.filterEntries";

    /// <summary>主人账号。</summary>
    public static string Owner(string platform) => $"access.{Normalize(platform)}.owner";

    /// <summary>主人昵称（只用于显示）。</summary>
    public static string OwnerName(string platform) => $"access.{Normalize(platform)}.ownerNickname";

    /// <summary>管理员列表（逗号分隔）。</summary>
    public static string Admins(string platform) => $"access.{Normalize(platform)}.admins";

    /// <summary>消息白名单（逗号分隔的 <c>{platform}:{g|p}id</c>）。</summary>
    public static string Whitelist(string platform) => $"access.{Normalize(platform)}.whitelist";

    /// <summary>消息黑名单。</summary>
    public static string Blacklist(string platform) => $"access.{Normalize(platform)}.blacklist";

    /// <summary>规范化平台名（空 → <see cref="DefaultPlatform"/>，统一小写）。</summary>
    public static string Normalize(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? DefaultPlatform : platform.Trim().ToLowerInvariant();

    /// <summary>这是否是"旧键适用的平台"（只有 <c>qq</c> 才有旧键可回退）。</summary>
    public static bool HasLegacyKeys(string platform) =>
        string.Equals(Normalize(platform), DefaultPlatform, StringComparison.Ordinal);
}
