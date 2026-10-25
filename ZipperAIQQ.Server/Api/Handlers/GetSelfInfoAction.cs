using System.Text.Json;
using ZipperAIQQ.Channel;
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

    public GetSelfInfoAction(
        IMessageChannel channel,
        ServerOptions options,
        IRuntimeSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        _channel = channel;
        _options = options;
        _settings = settings;
    }

    public string Name => "getSelfInfo";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var persisted = await _settings.GetAsync(SelfIdRecorder.StorageKey, ct).ConfigureAwait(false);

        return new
        {
            channelSelfId = _channel.SelfId,
            configuredSelfId = _options.SelfId,
            persistedSelfId = persisted,
            effectiveSelfId = _channel.SelfId ?? _options.SelfId ?? persisted,
        };
    }
}
