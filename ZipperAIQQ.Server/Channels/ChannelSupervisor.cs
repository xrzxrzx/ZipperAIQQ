using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Channel.Reliability;
using ZipperAIQQ.Server.Configuration;

namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 通道看门狗：为每个可恢复通道跑一条共用的恢复循环。
/// </summary>
/// <remarks>
/// <para>
/// 这里**没有平台分支**：QQ 的重拨、抖音的"等回连"都在各自的
/// <see cref="IRecoverableChannel.TryRecoverAsync"/> 里，退避与熔断是共用的一份
/// （<see cref="ChannelRecoveryLoop"/>），改动不会漂出两套行为。
/// </para>
/// <para>
/// 关停时随 <paramref name="stoppingToken"/> 一起退出；循环内部已处理取消，不会留后台任务。
/// </para>
/// </remarks>
public sealed class ChannelSupervisor : BackgroundService
{
    private readonly IReadOnlyList<IRecoverableChannel> _channels;
    private readonly ChannelHealthRegistry _health;
    private readonly ILogger _logger;

    private readonly ChannelReliabilityOptions _options;

    public ChannelSupervisor(
        IEnumerable<IRecoverableChannel> channels,
        ChannelHealthRegistry health,
        ChannelReliabilityOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _channels = channels.ToList();
        _health = health;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_channels.Count == 0)
        {
            _logger.Debug("没有可恢复通道，看门狗空转");
            return;
        }

        _logger.Information("通道看门狗启动：{Channels}", string.Join("、", _channels.Select(c => c.Platform)));

        var loops = _channels
            .Select(channel =>
            {
                var loop = new ChannelRecoveryLoop(
                    channel,
                    _health.Get(channel.Platform),
                    new ReconnectPolicy(
                        TimeSpan.FromSeconds(_options.BackoffBaseSeconds),
                        TimeSpan.FromSeconds(_options.BackoffMaxSeconds)),
                    _logger)
                {
                    PollInterval = TimeSpan.FromSeconds(_options.PollSeconds),
                };

                return loop.RunAsync(stoppingToken);
            })
            .ToArray();

        await Task.WhenAll(loops).ConfigureAwait(false);
    }
}
