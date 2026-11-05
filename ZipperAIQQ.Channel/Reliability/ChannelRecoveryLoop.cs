using Serilog;
using ZipperAIQQ.Channel.Failures;

namespace ZipperAIQQ.Channel.Reliability;

/// <summary>
/// 通道恢复循环：**所有平台共用一份**（QQ 重拨 WS、抖音等回连，只是 <see cref="IRecoverableChannel"/> 的实现不同）。
/// </summary>
/// <remarks>
/// <para>
/// 循环职责只有四件事：发现断开 → 按退避重试 → 记健康状态 → 恢复后留一条日志。
/// 具体「怎么恢复」交给通道自己，避免每个平台各写一套退避/熔断而后行为不一致。
/// </para>
/// <para>
/// 时钟与等待都做成可注入的：测试里不睡真实时间，也能验证「第几次等多久」。
/// </para>
/// </remarks>
public sealed class ChannelRecoveryLoop
{
    private readonly IRecoverableChannel _channel;
    private readonly ChannelHealthMonitor _monitor;
    private readonly ReconnectPolicy _policy;
    private readonly ILogger _logger;

    public ChannelRecoveryLoop(
        IRecoverableChannel channel,
        ChannelHealthMonitor monitor,
        ReconnectPolicy policy,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        _channel = channel;
        _monitor = monitor;
        _policy = policy;
        _logger = logger;
    }

    /// <summary>健康时的巡检间隔。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>时钟（测试可替换）。</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>等待（测试可替换成"立即返回"）。</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// 跑到取消为止。
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        DateTimeOffset? disconnectedAt = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_channel.IsConnected)
                {
                    if (disconnectedAt is { } since)
                    {
                        // 断开时长是「事后核对」的关键证据（验收口径要求日志里能看到）
                        var downtime = Clock() - since;
                        _monitor.RecordRecovered(downtime);
                        _logger.Information(
                            "{Platform} 通道已恢复：断开 {Seconds:F0} 秒",
                            _channel.Platform, downtime.TotalSeconds);
                    }

                    if (_monitor.RecordSuccess())
                    {
                        _logger.Information("{Platform} 通道已恢复（连续失败计数清零）", _channel.Platform);
                    }

                    disconnectedAt = null;
                    attempt = 0;
                    await Delay(PollInterval, ct).ConfigureAwait(false);
                    continue;
                }

                disconnectedAt ??= Clock();

                // 熔断冷却期内不试，等冷却结束再放一次半开探测
                if (_monitor.State == CircuitState.Open && !_monitor.TryHalfOpen(Clock()))
                {
                    await Delay(PollInterval, ct).ConfigureAwait(false);
                    continue;
                }

                bool recovered;
                try
                {
                    recovered = await _channel.TryRecoverAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "{Platform} 通道恢复失败", _channel.Platform);
                    recovered = false;
                }

                if (recovered)
                {
                    if (disconnectedAt is { } since)
                    {
                        _monitor.RecordRecovered(Clock() - since);
                    }

                    disconnectedAt = null;
                    _monitor.RecordSuccess();
                    attempt = 0;
                    _logger.Information("{Platform} 通道重连成功", _channel.Platform);
                    await Delay(PollInterval, ct).ConfigureAwait(false);
                    continue;
                }

                var openedNow = _monitor.RecordFailure(FailureKind.ChannelUnavailable, Clock());
                if (openedNow)
                {
                    _logger.Warning(
                        "{Platform} 通道连续失败达阈值，熔断 {Cooldown}（不再重试）",
                        _channel.Platform,
                        _monitor.OpenedAt);
                }

                var wait = _policy.DelayFor(attempt++);
                _logger.Warning(
                    "{Platform} 通道断开，第 {Attempt} 次重试将在 {Delay} 后",
                    _channel.Platform, attempt, wait);

                await Delay(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
