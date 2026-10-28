using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Settings;

using ZipperAIQQ.Server.Config;
namespace ZipperAIQQ.Server.Notices;

/// <summary>
/// 通知响应开关的**唯一读写口径**（键名 / 平台规范化 / 旧键回退 / qq 镜像）。
/// </summary>
/// <remarks>
/// 以前键拼法散在 API Handler、命令、通知响应三处：加平台或改回退规则时容易只改一处。
/// 现在所有读写都经过这里，<c>GetBehaviorAction</c> 只保留给客户端的展示逻辑。
/// </remarks>
public static class NoticeSettings
{
    public static string NormalizePlatform(string? platform) =>
        string.IsNullOrWhiteSpace(platform)
            ? NoticeCatalog.QqPlatform
            : platform.Trim().ToLowerInvariant();

    /// <summary>平台键：<c>notice.reply:{platform}:{type}</c>。</summary>
    public static string Key(string platform, NoticeType type) =>
        $"notice.reply:{NormalizePlatform(platform)}:{type}";

    /// <summary>单平台时代的旧键：<c>notice.reply:{type}</c>（只读回退 + qq 镜像写）。</summary>
    public static string LegacyKey(NoticeType type) => $"notice.reply:{type}";

    /// <summary>这个平台是否要镜像写旧键（只有 qq 有旧键）。</summary>
    public static bool HasLegacyKeys(string platform) =>
        string.Equals(NormalizePlatform(platform), NoticeCatalog.QqPlatform, StringComparison.Ordinal);

    /// <summary>字符串值 → 布尔（兼容历史写入的 1/true/True）。</summary>
    public static bool IsOn(string? value) => SettingsValue.IsTrue(value);

    /// <summary>读开关：新键存在（含空串）就用新键；仅 qq 且从未写过才回退旧键。</summary>
    public static async Task<string?> ReadAsync(
        IRuntimeSettingsStore settings,
        string platform,
        NoticeType type,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var value = await settings.GetAsync(Key(platform, type), ct).ConfigureAwait(false);
        if (value is not null)
        {
            return value;
        }

        return HasLegacyKeys(platform)
            ? await settings.GetAsync(LegacyKey(type), ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>写开关：只写平台键；qq 额外镜像旧键。</summary>
    public static async Task WriteAsync(
        IRuntimeSettingsStore settings,
        string platform,
        NoticeType type,
        bool enabled,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var flag = enabled ? "1" : "0";

        await settings.SetAsync(Key(platform, type), flag, ct).ConfigureAwait(false);

        if (HasLegacyKeys(platform))
        {
            await settings.SetAsync(LegacyKey(type), flag, ct).ConfigureAwait(false);
        }
    }
}
