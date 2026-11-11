using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Server.Alerts;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Schedules;

namespace ZipperAIQQ.Server.Scheduler;

/// <summary>
/// 定时任务轮询（FR-14 / D21：**数据库即队列**，1 分钟精度）。
/// </summary>
/// <remarks>
/// <para>
/// 不另建内存队列 —— 那会引入「DB 与内存两份真相」。这里每分钟只做一件事：
/// 「查到期 → 发出去 → 推进时间」，全部状态都在库里。
/// </para>
/// <para>
/// **漏执行补偿（FR-14.5）**：启动时先跑一次 Tick，停机期间到期的任务会被执行；
/// 周期任务**只补最近一次** —— 错过 3 次也只发 1 条，然后从当前时间重新计，不会雪崩。
/// </para>
/// </remarks>
public sealed class SchedulePoller : BackgroundService
{
    /// <summary>通道熔断时，定时任务推后多少分钟再试。</summary>
    private const int ChannelDownRetryMinutes = 5;

    /// <summary>轮询间隔（FR-14.2：1 分钟，精度到分钟）。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>一轮最多处理多少条，防止单轮卡太久。</summary>
    private const int BatchSize = 50;

    private readonly IScheduleStore _store;
    private readonly IScheduleExecutor _executor;
    private readonly ILogger _logger;
    private readonly IAlertSink? _alerts;

    /// <summary>每个任务的失败次数与下次可重试时间（内存态，重启即清）。</summary>
    private readonly ConcurrentDictionary<long, RetryState> _retry = new();

    private readonly record struct RetryState(int Attempts, DateTimeOffset NextAt);

    public SchedulePoller(
        IScheduleStore store,
        IScheduleExecutor executor,
        ILogger logger,
        IAlertSink? alerts = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _executor = executor;
        _logger = logger;
        _alerts = alerts;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("定时任务轮询已启动（每 {Seconds} 秒一次）", (int)TickInterval.TotalSeconds);

        // 启动即补一次：停机期间错过的任务不该永远丢掉
        await TickAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var due = await _store.GetDueAsync(now, BatchSize, ct).ConfigureAwait(false);
            foreach (var task in due)
            {
                // 退避中：跳过，别每分钟再撞一次
                if (_retry.TryGetValue(task.Id, out var state) && state.NextAt > now)
                {
                    continue;
                }

                await RunAsync(task, now, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停机
        }
        catch (Exception ex)
        {
            // 一轮失败不能把轮询器打死
            _logger.Error(ex, "定时任务轮询失败，下一轮重试");
        }
    }

    private async Task RunAsync(ScheduledTask task, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            // G-2：到点不再直发，投给目标会话的 Agent 决定（D-08）
            var result = await _executor.ExecuteAsync(task, ct).ConfigureAwait(false);
            _logger.Information(
                "定时任务 #{Id} 执行完成：{Outcome}（会话 {SessionId}）",
                task.Id, result.Outcome, task.Chat.SessionId);

            if (result.Outcome == ScheduleExecutionOutcome.ChannelDownSkipped)
            {
                // 熔断期间不消耗重试次数，也不每分钟重试：推后 5 分钟再看
                await _store
                    .RescheduleAsync(task.Id, now.AddMinutes(ChannelDownRetryMinutes), ct)
                    .ConfigureAwait(false);
                _retry.TryRemove(task.Id, out _);
                return;
            }
        }
        catch (Exception ex)
        {
            var attempts = _retry.TryGetValue(task.Id, out var previous) ? previous.Attempts + 1 : 1;

            if (attempts >= ScheduleRetryPolicy.MaxAttempts)
            {
                _retry.TryRemove(task.Id, out _);
                await _store.MarkFailedAsync(task.Id, ct).ConfigureAwait(false);
                _logger.Error(
                    ex, "定时任务 #{Id} 连续失败 {Attempts} 次，已停用（需人工处理）",
                    task.Id, attempts);

                if (_alerts is not null)
                {
                    await _alerts.RaiseAsync(new AlertMessage(
                        AlertKind.ScheduleFailBurst,
                        task.Chat.Platform,
                        $"⛔ 定时任务 #{task.Id} 连续失败 {attempts} 次，已停止重试"
                        + $"（会话 {task.Chat.SessionId}）：{ex.Message}",
                        BeijingTime.Now), ct).ConfigureAwait(false);
                }

                return;
            }

            var delay = ScheduleRetryPolicy.DelayFor(attempts);
            _retry[task.Id] = new RetryState(attempts, now + delay);
            _logger.Error(
                ex, "定时任务 #{Id} 发送失败（{SessionId}），第 {Attempts} 次；{Delay} 后重试",
                task.Id, task.Chat.SessionId, attempts, delay);
            return;
        }

        _retry.TryRemove(task.Id, out _);

        if (task.Repeat == ScheduleRepeat.Interval)
        {
            // 只补最近一次：错过多次也只发一条，然后从"现在"重新计
            var interval = Math.Max(1, task.IntervalMinutes);
            await _store.RescheduleAsync(task.Id, now.AddMinutes(interval), ct).ConfigureAwait(false);

            _logger.Information(
                "定时任务 #{Id} 已发送（周期 {Minutes} 分钟，会话 {SessionId}），下次 {Next}",
                task.Id, interval, task.Chat.SessionId, NextRunText(now, interval));
        }
        else
        {
            await _store.MarkDoneAsync(task.Id, ct).ConfigureAwait(false);
            _logger.Information("定时任务 #{Id} 已发送（一次性，会话 {SessionId}）", task.Id, task.Chat.SessionId);
        }
    }

    private static string NextRunText(DateTimeOffset now, int minutes) =>
        Core.Time.BeijingTime.Format(now.AddMinutes(minutes));
}
