using System.Collections.Concurrent;
using Serilog;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Server.Orchestration;

/// <summary>轮次状态（R-04 / R-10，K-2）。</summary>
public enum TurnState
{
    Idle = 0,
    Running = 1,
    Suspended = 2,
}

/// <summary>收轮原因（静默审计用）。</summary>
public enum TurnEndReason
{
    /// <summary>挂起超时，正常收轮。</summary>
    Suspended = 0,

    /// <summary>没有启用挂起，处理完即收轮。</summary>
    Immediate = 1,

    /// <summary>取消（停机）。</summary>
    Cancelled = 2,

    /// <summary>处理过程抛异常。</summary>
    Failed = 3,
}

/// <summary>轮次配置（拍板：挂起 3 分钟、静默收轮）。</summary>
public sealed record TurnOptions
{
    public bool SuspendedEnabled { get; init; } = true;

    public TimeSpan SuspendTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

/// <summary>
/// 会话轮次协调器（K-2）：同一会话**永不并行两轮**，一轮可跨空挂起等待下一条消息。
/// </summary>
/// <remarks>
/// <para>
/// 设计取舍：不做「长生命周期轮」的复杂状态机，而是把「一轮」定义为
/// 「连续处理若干批消息，直到挂起超时」。这样：
/// 没有消息就等着，等到了就继续同一轮，等超时就收轮。
/// </para>
/// <para>
/// 队列语义（K-3 起）：运行/挂起中到达的消息进 <c>_pending</c>；
/// 私聊默认 steer（本轮下一次模型调用前注入），群聊由 JEV 决定（后续接入）。
/// 本类只保证**串行与不丢**，注入策略由调用方决定。
/// </para>
/// </remarks>
public sealed class TurnCoordinator
{
    private readonly TurnOptions _options;
    private readonly IMetricsStore _metrics;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SessionTurn> _turns = new(StringComparer.Ordinal);

    public TurnCoordinator(TurnOptions options, IMetricsStore metrics, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

    private sealed class SessionTurn
    {
        public readonly object Gate = new();
        public readonly List<IncomingMessage> Pending = [];
        public TaskCompletionSource<bool>? Wakeup;
        public TurnState State = TurnState.Idle;
        public int FollowUpCount;
    }

    /// <summary>
    /// 提交一批消息。若该会话正在跑/挂起，则并入队列（followUp），由正在跑的循环接手。
    /// </summary>
    public void Submit(
        ChatRef chat,
        IReadOnlyList<IncomingMessage> batch,
        Func<IReadOnlyList<IncomingMessage>, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(handler);

        if (batch.Count == 0)
        {
            return;
        }

        var turn = _turns.GetOrAdd(chat.SessionId, _ => new SessionTurn());

        lock (turn.Gate)
        {
            if (turn.State == TurnState.Idle)
            {
                turn.State = TurnState.Running;
                turn.Pending.AddRange(batch);
                _ = Task.Run(() => RunLoopAsync(chat, turn, handler, ct), CancellationToken.None);
                return;
            }

            // 运行/挂起中：并入队列。挂起中的等待者被唤醒 → 继续同一轮
            turn.Pending.AddRange(batch);
            turn.FollowUpCount += batch.Count;
            turn.Wakeup?.TrySetResult(true);

            _logger.Debug(
                "会话 {Session} 在 {State} 状态收到 {Count} 条，已并入当前轮（累计 {FollowUp} 条）",
                chat.SessionId, turn.State, batch.Count, turn.FollowUpCount);
        }
    }

    private async Task RunLoopAsync(
        ChatRef chat,
        SessionTurn turn,
        Func<IReadOnlyList<IncomingMessage>, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var reason = TurnEndReason.Immediate;
        var restart = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                List<IncomingMessage> current;
                lock (turn.Gate)
                {
                    if (turn.Pending.Count == 0)
                    {
                        break;
                    }

                    current = [.. turn.Pending];
                    turn.Pending.Clear();
                    turn.State = TurnState.Running;
                }

                await handler(current, ct).ConfigureAwait(false);

                if (!_options.SuspendedEnabled)
                {
                    break;
                }

                TaskCompletionSource<bool> wakeup;
                lock (turn.Gate)
                {
                    if (turn.Pending.Count > 0)
                    {
                        continue;   // 处理期间又来了消息：直接续轮
                    }

                    wakeup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    turn.Wakeup = wakeup;
                    turn.State = TurnState.Suspended;
                }

                await RecordAsync(chat.SessionId, "turn.suspended", 0, ct).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_options.SuspendTimeout);

                try
                {
                    await wakeup.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    reason = TurnEndReason.Suspended;
                    break;   // 挂起超时 → 收轮
                }
                finally
                {
                    lock (turn.Gate)
                    {
                        turn.Wakeup = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = TurnEndReason.Cancelled;
        }
        catch (Exception ex)
        {
            reason = TurnEndReason.Failed;
            _logger.Error(ex, "会话 {Session} 的轮次处理失败", chat.SessionId);
        }
        finally
        {
            var elapsed = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

            lock (turn.Gate)
            {
                // 收轮与「新消息」之间的竞态：有积压就**保持 Running**直接续跑，
                // 不能先回 Idle —— 否则 Submit 会另起一条循环，两条循环并发处理同一会话。
                restart = turn.Pending.Count > 0;
                turn.State = restart ? TurnState.Running : TurnState.Idle;
                turn.Wakeup = null;
            }

            await RecordAsync(chat.SessionId, "turn.duration", elapsed, CancellationToken.None).ConfigureAwait(false);
            _logger.Information(
                "会话 {Session} 收轮：原因 {Reason}，耗时 {Elapsed:F0}ms，followUp {FollowUp} 条",
                chat.SessionId, reason, elapsed, turn.FollowUpCount);

            turn.FollowUpCount = 0;

            // 收轮瞬间又来了消息：保持 Running 续跑（上面已判定），不能丢
        }

        if (restart)
        {
            _ = Task.Run(() => RunLoopAsync(chat, turn, handler, CancellationToken.None), CancellationToken.None);
        }
    }

    private async Task RecordAsync(string sessionId, string kind, double value, CancellationToken ct)
    {
        try
        {
            await _metrics.RecordAsync(new MetricEntry(sessionId, kind, value), ct).ConfigureAwait(false);
        }
        catch
        {
            // 指标是观测，不是功能
        }
    }
}
