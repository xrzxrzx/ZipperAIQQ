using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Owners;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Access;

/// <summary>
/// 「设置主人」的唯一写入口（HTTP <c>setOwner</c> 与 <c>/owner</c> 命令共用）。
/// </summary>
/// <remarks>
/// 以前两个入口各写一遍：命令漏了 <see cref="IOwnerScope"/> 刷新，
/// 转移主人后放行范围要等后台轮询才更新。集中到这里后两边行为一致：
/// 写新键 + qq 镜像旧键 + 解析器立即生效 + 触发范围刷新 + 审计。
/// </remarks>
public sealed class OwnerService
{
    private readonly IRuntimeSettingsStore _settings;
    private readonly IRoleResolver _roles;
    private readonly IOwnerScope _ownerScope;
    private readonly IConfigStore _configs;
    private readonly IOwnerAccountStore? _accounts;

    public OwnerService(
        IRuntimeSettingsStore settings,
        IRoleResolver roles,
        IOwnerScope ownerScope,
        IConfigStore configs,
        IOwnerAccountStore? accounts = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(ownerScope);
        ArgumentNullException.ThrowIfNull(configs);

        _settings = settings;
        _roles = roles;
        _ownerScope = ownerScope;
        _configs = configs;
        _accounts = accounts;
    }

    /// <summary>写入主人并立即生效。</summary>
    /// <param name="platform">平台（已规范化）。</param>
    /// <param name="ownerId">新主人账号（非空）。</param>
    /// <param name="nickname">显示名；没给就写空串。</param>
    /// <param name="actor">审计主体。</param>
    public async Task SetOwnerAsync(
        string platform,
        string ownerId,
        string? nickname,
        string actor,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        await _settings.SetAsync(AccessKeys.Owner(platform), ownerId, ct).ConfigureAwait(false);
        await _settings
            .SetAsync(AccessKeys.OwnerName(platform), nickname ?? string.Empty, ct)
            .ConfigureAwait(false);

        // 旧键也同步一份（仅 qq）：万一回退到旧版程序，主人不会突然"消失"
        if (AccessKeys.HasLegacyKeys(platform))
        {
            await _settings.SetAsync(AccessKeys.LegacyOwnerKey, ownerId, ct).ConfigureAwait(false);
            await _settings
                .SetAsync(AccessKeys.LegacyOwnerNameKey, nickname ?? string.Empty, ct)
                .ConfigureAwait(false);
        }

        // D4-a：统一身份表是**主数据**，settings 是兼容镜像 —— 两边都写，读侧以身份表为准
        if (_accounts is not null)
        {
            try
            {
                await _accounts
                    .UpsertAsync([new OwnerAccount(platform, ownerId)], ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 镜像写失败不阻塞设置主人（下次告警/启动会再补）
            }
        }

        // 立即生效：权限解析器持有内存里的各平台 owner
        if (_roles is ConfiguredRoleResolver configured)
        {
            configured.SetOwner(platform, ownerId);
        }

        // 主人变了 → 放行范围也要跟着变（私聊 + 主人所在的群）。
        // 不 await：解析群成员可能是几千毫秒，别让保存主人卡住；后台服务失败会自己重试。
        _ = _ownerScope.RefreshAsync();

        await _configs
            .AppendAuditAsync("setOwner", actor, $"{platform}:owner={ownerId}", ct)
            .ConfigureAwait(false);
    }
}
