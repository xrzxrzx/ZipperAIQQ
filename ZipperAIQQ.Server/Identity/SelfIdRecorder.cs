using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Identity;

/// <summary>
/// 把通道发现的机器人账号持久化到 <c>bot.selfId</c>（问题记录 02 #3）。
/// </summary>
/// <remarks>
/// 动态 <c>self_id</c> 只在连接后才有；重启时如果配置里没写 selfId，
/// 读回这个键就能继续正确过滤自激，不必每次依赖环境变量。
/// </remarks>
public sealed class SelfIdRecorder : BackgroundService
{
    public const string StorageKey = "bot.selfId";

    private readonly IMessageChannel _channel;
    private readonly IRuntimeSettingsStore _settings;
    private readonly ILogger _logger;

    public SelfIdRecorder(
        IMessageChannel channel,
        IRuntimeSettingsStore settings,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _channel = channel;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnSelfId(string selfId) => _ = PersistAsync(selfId);

        _channel.SelfIdChanged += OnSelfId;
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        finally
        {
            _channel.SelfIdChanged -= OnSelfId;
        }
    }

    private async Task PersistAsync(string selfId)
    {
        try
        {
            await _settings.SetAsync(StorageKey, selfId, CancellationToken.None).ConfigureAwait(false);
            _logger.Information("机器人账号已持久化：{SelfId}", selfId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "持久化机器人账号失败：{SelfId}", selfId);
        }
    }
}
