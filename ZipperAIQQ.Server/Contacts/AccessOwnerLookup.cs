using ZipperAIQQ.Agent;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Server.Access;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Contacts;

/// <summary>
/// <see cref="IOwnerLookup"/> 的实现：直接复用 <see cref="AccessSettings"/>。
/// </summary>
/// <remarks>
/// 这里**不重复实现键名与旧键回退**：<c>access.{platform}.owner</c> 的读写口径
/// （先新键、空了回退旧键）已经由 <see cref="AccessSettings"/> 统一维护，
/// 再写一份必然漂移 —— 2026-10-28 的 bug 就是 Agent 里写死了旧键 <c>owner.userId</c>，
/// 结果抖音会话里"主人"永远认不出来。
/// </remarks>
public sealed class AccessOwnerLookup : IOwnerLookup
{
    private readonly IRuntimeSettingsStore _settings;
    private readonly IPlatformContacts? _platforms;

    public AccessOwnerLookup(IRuntimeSettingsStore settings, IPlatformContacts? platforms = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _platforms = platforms;
    }

    public async Task<(string? OwnerId, string? OwnerName)> GetAsync(
        string platform,
        CancellationToken ct = default)
    {
        var ownerId = await AccessSettings.GetOwnerAsync(_settings, platform, ct).ConfigureAwait(false);
        var ownerName = await AccessSettings.GetOwnerNameAsync(_settings, platform, ct).ConfigureAwait(false);

        return (string.IsNullOrWhiteSpace(ownerId) ? null : ownerId.Trim(),
                string.IsNullOrWhiteSpace(ownerName) ? null : ownerName.Trim());
    }

    /// <summary>枚举已接入平台，逐个取主人（R-08：跨平台联想）。</summary>
    public async Task<IReadOnlyList<OwnerAccountRef>> GetAllAsync(CancellationToken ct = default)
    {
        if (_platforms is null)
        {
            return [];
        }

        var list = new List<OwnerAccountRef>();
        foreach (var platform in _platforms.Platforms)
        {
            var (ownerId, ownerName) = await GetAsync(platform, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ownerId))
            {
                list.Add(new OwnerAccountRef(platform, ownerId!, ownerName));
            }
        }

        return list;
    }
}
