using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Server.Alerts;

namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 抖音 sidecar 健康看护（2026-11-11 故障后补）。
/// </summary>
/// <remarks>
/// <para>
/// 通道看门狗只管「服务端能不能连上 sidecar」，管不到「sidecar 进程还活着但卡死」。
/// 这里补一层：持续断开超过阈值 → 告警（节流）+ 可选自动重启。
/// </para>
/// <para>
/// 自动重启通过环境变量 <c>ZIPPER_SIDECAR_RESTART_CMD</c> 配置（例如
/// <c>schtasks /end /tn ZipperAIQQ-DouyinSidecar &amp; schtasks /run /tn ZipperAIQQ-DouyinSidecar</c>）；
/// 没配就只告警，不猜平台怎么重启进程 —— 服务端要保持跨平台。
/// </para>
/// </remarks>
public sealed class SidecarHealthMonitor : BackgroundService
{
    private readonly IChannelLogin _douyin;
    private readonly IAlertSink _alerts;
    private readonly ILogger _logger;
    private readonly TimeSpan _alertAfter;
    private readonly TimeSpan _restartCooldown;
    private readonly string? _restartCommand;
    // 节流用「本次检查的时间」，而不是系统时钟 —— 否则测试与回放时判定不一致
    private DateTimeOffset _now;
    private readonly AlertThrottle _throttle;
    private DateTimeOffset? _downSince;
    private DateTimeOffset? _lastRestartAt;

    public SidecarHealthMonitor(
        IChannelLogin douyin,
        IAlertSink alerts,
        ILogger logger,
        TimeSpan? alertAfter = null,
        TimeSpan? restartCooldown = null,
        string? restartCommand = null)
    {
        ArgumentNullException.ThrowIfNull(douyin);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(logger);

        _douyin = douyin;
        _alerts = alerts;
        _logger = logger;
        _alertAfter = alertAfter ?? TimeSpan.FromMinutes(3);
        _restartCooldown = restartCooldown ?? TimeSpan.FromMinutes(10);
        _restartCommand = restartCommand ?? Environment.GetEnvironmentVariable("ZIPPER_SIDECAR_RESTART_CMD");
        _throttle = new AlertThrottle(TimeSpan.FromMinutes(10), () => _now);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information(
            "sidecar 健康看护启动：断开 {Minutes} 分钟后告警{Action}",
            _alertAfter.TotalMinutes,
            string.IsNullOrWhiteSpace(_restartCommand) ? "" : "并尝试自动重启");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckOnce(DateTimeOffset.UtcNow);
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "sidecar 健康检查失败（下一轮继续）");
            }
        }
    }

    /// <summary>单次检查（拆出来便于测试）。</summary>
    public void CheckOnce(DateTimeOffset now)
    {
        _now = now;

        if (_douyin.IsConnected)
        {
            if (_downSince is not null)
            {
                _logger.Information("sidecar 已恢复（断开 {Seconds:F0} 秒）",
                    (now - _downSince.Value).TotalSeconds);
            }

            _downSince = null;
            return;
        }

        _downSince ??= now;
        var downFor = now - _downSince.Value;

        if (downFor < _alertAfter)
        {
            return;
        }

        if (_throttle.TryAcquire("sidecar:down"))
        {
            _logger.Error("sidecar 已断开 {Minutes:F0} 分钟，触发告警与重启检查", downFor.TotalMinutes);
            _ = _alerts.RaiseAsync(new AlertMessage(
                AlertKind.ChannelDown,
                "douyin",
                $"⚠️ 抖音 sidecar 已断开 {downFor.TotalMinutes:F0} 分钟（进程可能卡死）"
                + (string.IsNullOrWhiteSpace(_restartCommand) ? "。" : "，正在尝试自动重启。"),
                BeijingTime.Now), CancellationToken.None);
        }

        TryRestart(now, downFor);
    }

    private void TryRestart(DateTimeOffset now, TimeSpan downFor)
    {
        if (string.IsNullOrWhiteSpace(_restartCommand))
        {
            return;
        }

        if (_lastRestartAt is { } last && now - last < _restartCooldown)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.Warning("已配置 sidecar 重启命令，但当前不是 Windows，跳过（请外部守护）");
            return;
        }

        try
        {
            _lastRestartAt = now;
            _logger.Error("尝试重启 sidecar（已断开 {Minutes:F0} 分钟）：{Command}",
                downFor.TotalMinutes, _restartCommand);

            System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + _restartCommand,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "启动 sidecar 重启命令失败");
        }
    }
}
