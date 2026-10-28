using System.Text.Json;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Server.Access;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 主人设定（第 17 项）：读当前主人 + 好友候选列表，**按平台**（S3-C）。
/// </summary>
public sealed class GetOwnerAction : IApiAction
{
    private readonly IRuntimeSettingsStore _settings;
    private readonly IContactDirectoryRegistry _directories;
    private readonly Serilog.ILogger _logger;

    public GetOwnerAction(
        IRuntimeSettingsStore settings,
        IContactDirectoryRegistry directories,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _directories = directories;
        _logger = logger;
    }

    public string Name => "getOwner";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var platform = AccessKeys.Normalize(PayloadReader.GetString(payload, "platform"));

        var ownerId = await AccessSettings.GetOwnerAsync(_settings, platform, ct).ConfigureAwait(false);
        var ownerName = await AccessSettings.GetOwnerNameAsync(_settings, platform, ct).ConfigureAwait(false);

        // 好友列表用于「选一个主人」；查不到就返回空（不阻断页面）
        // 按平台取：抖音的好友不能拿来当 QQ 的主人（决策四）
        var directory = _directories.For(platform);
        IReadOnlyList<ContactInfo> friends = [];
        if (directory is not null)
        {
            try
            {
                friends = await directory.GetFriendsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "读取 {Platform} 好友列表失败，主人候选置空", platform);
                friends = [];
            }
        }

        return new
        {
            platform,
            platforms = _directories.Platforms,
            ownerId,
            ownerName,
            candidates = friends
                .Select(f => new
                {
                    userId = f.UserId,
                    nickname = string.IsNullOrWhiteSpace(f.Remark) ? f.Nickname : $"{f.Nickname}（{f.Remark}）",
                })
                .ToList(),
        };
    }
}

/// <summary>
/// 设置主人（第 17 项）：写库 + **立即生效**（同步更新权限解析器）。
/// </summary>
public sealed class SetOwnerAction : IApiAction
{
    private readonly IRuntimeSettingsStore _settings;
    private readonly IRoleResolver _roles;
    private readonly IOwnerScope _ownerScope;
    private readonly Storage.Config.IConfigStore _configs;

    public SetOwnerAction(
        IRuntimeSettingsStore settings,
        IRoleResolver roles,
        IOwnerScope ownerScope,
        Storage.Config.IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(ownerScope);
        ArgumentNullException.ThrowIfNull(configs);

        _settings = settings;
        _roles = roles;
        _ownerScope = ownerScope;
        _configs = configs;
    }

    public string Name => "setOwner";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var userId = PayloadReader.GetString(payload, "userId")?.Trim();
        if (string.IsNullOrEmpty(userId))
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 userId（要设为主人的好友账号）。");
        }

        var nickname = PayloadReader.GetString(payload, "nickname");

        var platform = AccessKeys.Normalize(PayloadReader.GetString(payload, "platform"));

        await _settings.SetAsync(AccessKeys.Owner(platform), userId, ct).ConfigureAwait(false);
        await _settings.SetAsync(AccessKeys.OwnerName(platform), nickname ?? string.Empty, ct).ConfigureAwait(false);

        // 旧键也同步一份（仅 qq）：万一回退到旧版程序，主人不会突然"消失"
        if (AccessKeys.HasLegacyKeys(platform))
        {
            await _settings.SetAsync(AccessKeys.LegacyOwnerKey, userId, ct).ConfigureAwait(false);
            await _settings.SetAsync(AccessKeys.LegacyOwnerNameKey, nickname ?? string.Empty, ct).ConfigureAwait(false);
        }

        // 立即生效：权限解析器持有内存里的各平台 owner
        if (_roles is ConfiguredRoleResolver configured)
        {
            configured.SetOwner(platform, userId);
        }

        await _configs
            .AppendAuditAsync("setOwner", "http-client", $"{platform}:owner={userId}", ct)
            .ConfigureAwait(false);

        // 主人变了 → 放行范围也要跟着变（私聊 + 主人所在的群）。
        // 不 await：解析群成员可能是几千毫秒，别让保存主人卡住；后台服务失败会自己重试。
        _ = _ownerScope.RefreshAsync();

        return new { ownerId = userId, ownerName = nickname, platform, effective = "immediate" };
    }
}
