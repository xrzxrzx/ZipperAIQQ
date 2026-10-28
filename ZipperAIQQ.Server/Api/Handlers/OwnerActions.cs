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
                    nickname = ContactDisplay.Name(f),
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
    private readonly OwnerService _owners;

    public SetOwnerAction(OwnerService owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        _owners = owners;
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

        await _owners
            .SetOwnerAsync(platform, userId, nickname, "http-client", ct)
            .ConfigureAwait(false);

        return new { ownerId = userId, ownerName = nickname, platform, effective = "immediate" };
    }
}
