using System.Text.Json;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Server.Access;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Identity;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 自身账号诊断（问题记录 02 #3）：一眼看清通道 / 配置 / 持久化三个来源是否一致。
/// </summary>
public sealed class GetSelfInfoAction : IApiAction
{
    private readonly IMessageChannel _channel;
    private readonly ServerOptions _options;
    private readonly IRuntimeSettingsStore _settings;
    private readonly ISelfIdRegistry _selfIds;

    public GetSelfInfoAction(
        IMessageChannel channel,
        ServerOptions options,
        IRuntimeSettingsStore settings,
        ISelfIdRegistry selfIds)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(selfIds);

        _channel = channel;
        _options = options;
        _settings = settings;
        _selfIds = selfIds;
    }

    public string Name => "getSelfInfo";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var platform = AccessKeys.Normalize(PayloadReader.GetString(payload, "platform"));

        var persisted = await _settings
            .GetAsync(SelfIdRecorder.PlatformStorageKey(platform), ct)
            .ConfigureAwait(false);

        if (persisted is null && AccessKeys.HasLegacyKeys(platform))
        {
            persisted = await _settings.GetAsync(SelfIdRecorder.StorageKey, ct).ConfigureAwait(false);
        }

        var channelSelfId = _channel.SelfIdFor(platform) ?? _channel.SelfId;
        var configuredSelfId = _options.SelfIdFor(platform);

        return new
        {
            platform,
            channelSelfId,
            configuredSelfId,
            persistedSelfId = persisted,
            effectiveSelfId = channelSelfId
                ?? configuredSelfId
                ?? _selfIds.Get(platform)
                ?? persisted,
        };
    }
}
