using Microsoft.Extensions.Hosting;
using Serilog;
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
    /// <summary>轮询间隔（FR-14.2：1 分钟，精度到分钟）。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>一轮最多处理多少条，防止单轮卡太久。</summary>
    private const int BatchSize = 50;

    private readonly IScheduleStore _store;
    private readonly IMessageChannel _channel;
    private readonly IMessageStore _messages;
    private readonly ILogger _logger;

    public SchedulePoller(
        IScheduleStore store,
        IMessageChannel channel,
        IMessageStore messages,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _channel = channel;
        _messages = messages;
        _logger = logger;
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
        var outgoing = new OutgoingMessage
        {
            Chat = task.Chat,
            Segments = BuildSegments(task),
        };

        try
        {
            await _channel.SendAsync(outgoing, ct).ConfigureAwait(false);

            try
            {
                await _messages
                    .AppendOutgoingAsync(outgoing, ChatAgent.BotUserId, now, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 已发出，落库失败不改变事实
                _logger.Warning(ex, "定时任务 #{Id} 落库失败", task.Id);
            }
        }
        catch (Exception ex)
        {
            // 发送失败：**不推进时间**，下一轮会重试
            _logger.Error(ex, "定时任务 #{Id} 发送失败（会话 {SessionId}），下一轮重试", task.Id, task.Chat.SessionId);
            return;
        }

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

    private static List<MessageSegment> BuildSegments(ScheduledTask task)
    {
        var segments = new List<MessageSegment>();

        if (!string.IsNullOrWhiteSpace(task.MentionUserId))
        {
            segments.Add(new MentionSegment(task.MentionUserId));
            segments.Add(new TextSegment(" "));
        }

        segments.Add(new TextSegment(task.Content));
        return segments;
    }
}
