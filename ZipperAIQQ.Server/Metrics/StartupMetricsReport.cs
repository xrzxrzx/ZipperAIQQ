using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Server.Metrics;

/// <summary>
/// 启动后的历史指标报告（2026-11-11 起改为**后台异步**）。
/// </summary>
/// <remarks>
/// <para>
/// 以前它在 <c>app.Run()</c> 之前同步算「全量历史」（windowHours: 0），
/// 4 天数据就要 75 秒 —— 这期间端口已经监听但请求处理不了，
/// 看起来就像「服务没起来」或「客户端连不上」，重启时极容易被误判。
/// </para>
/// <para>
/// 现在：宿主启动完再跑、只统计最近 24 小时；日志里会标明窗口，避免把「24h 数字」当成全量。
/// </para>
/// </remarks>
public sealed class StartupMetricsReport : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private const int WindowHours = 24;

    private readonly IMetricsStore _metrics;

    public StartupMetricsReport(IMetricsStore metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);

            var summary = await _metrics.GetSummaryAsync(WindowHours, null, stoppingToken).ConfigureAwait(false);
            if (summary.Count == 0)
            {
                Log.Information("调用指标（最近 {Hours}h）：暂无数据", WindowHours);
                return;
            }

            Log.Information("调用指标（最近 {Hours}h，按平均耗时降序）：", WindowHours);
            foreach (var item in summary)
            {
                Log.Information(
                    "  {Kind,-22} {Count,6} 次  平均 {Avg,8:F1}ms  范围 {Min:F1}~{Max:F1}ms  token {In}/{Out}",
                    item.Kind, item.Count, item.AvgMs, item.MinMs, item.MaxMs,
                    item.TotalInputTokens, item.TotalOutputTokens);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "启动指标报告失败（不影响服务）");
        }
    }
}
