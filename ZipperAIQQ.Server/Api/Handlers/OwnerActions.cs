using System.Text.Json;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 主人设定（第 17 项）：读当前主人 + 好友候选列表。
/// </summary>
public sealed class GetOwnerAction : IApiAction
{
    public const string OwnerKey = "owner.userId";
    public const string OwnerNameKey = "owner.nickname";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IRoleResolver _roles;
    private readonly IContactDirectory _contacts;

    public GetOwnerAction(IRuntimeSettingsStore settings, IRoleResolver roles, IContactDirectory contacts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(contacts);

        _settings = settings;
        _roles = roles;
        _contacts = contacts;
    }

    public string Name => "getOwner";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var ownerId = await _settings.GetAsync(OwnerKey, ct).ConfigureAwait(false);
        var ownerName = await _settings.GetAsync(OwnerNameKey, ct).ConfigureAwait(false);

        // 好友列表用于「选一个主人」；查不到就返回空（不阻断页面）
        IReadOnlyList<ContactInfo> friends;
        try
        {
            friends = await _contacts.GetFriendsAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            friends = [];
        }

        return new
        {
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
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("userId", out var idElement) ||
            idElement.GetString() is not { Length: > 0 } userId)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 userId（要设为主人的好友账号）。");
        }

        userId = userId.Trim();
        var nickname = payload.TryGetProperty("nickname", out var nameElement)
            ? nameElement.GetString()
            : null;

        await _settings.SetAsync(GetOwnerAction.OwnerKey, userId, ct).ConfigureAwait(false);
        await _settings.SetAsync(GetOwnerAction.OwnerNameKey, nickname ?? string.Empty, ct).ConfigureAwait(false);

        // 立即生效：权限解析器持有一个内存里的 owner
        if (_roles is ConfiguredRoleResolver configured)
        {
            configured.SetOwner(userId);
        }

        await _configs
            .AppendAuditAsync("setOwner", "http-client", $"owner={userId}", ct)
            .ConfigureAwait(false);

        // 主人变了 → 放行范围也要跟着变（私聊 + 主人所在的群）。
        // 不 await：解析群成员可能是几千毫秒，别让保存主人卡住；后台服务失败会自己重试。
        _ = _ownerScope.RefreshAsync();

        return new { ownerId = userId, ownerName = nickname, effective = "immediate" };
    }
}
