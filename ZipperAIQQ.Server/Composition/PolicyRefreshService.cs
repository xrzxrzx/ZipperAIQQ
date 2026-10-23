using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Agent.Security;

namespace ZipperAIQQ.Server.Composition;

/// <summary>
/// 定期刷新执行策略快照（工具 / 命令 / NapCat API 的开关与角色）。
/// </summary>
/// <remarks>
/// 写策略时已经会 <c>ReloadAsync</c>；这个定时刷新只是兜底 ——
/// 万一有人直接改了库（或另一个客户端写失败），60 秒内也会对齐。
/// </remarks>
public sealed class PolicyRefreshService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IExecutionPolicy _policy;
    private readonly ILogger _logger;

    public PolicyRefreshService(IExecutionPolicy policy, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(logger);

        _policy = policy;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _policy.ReloadAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "刷新执行策略失败（沿用上一份快照）");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
