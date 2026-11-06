using Serilog;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Orchestration;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Tests.Server;

/// <summary>K-2：同一会话串行、跑/挂起中批次并入当前轮、挂起超时收轮。</summary>
public sealed class TurnCoordinatorTests
{
    private sealed class FakeMetrics : IMetricsStore
    {
        public List<string> Kinds { get; } = [];

        public Task RecordAsync(MetricEntry entry, CancellationToken ct = default)
        {
            lock (Kinds)
            {
                Kinds.Add(entry.Kind);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MetricSummary>> GetSummaryAsync(
            int windowHours = 24, string? sessionId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetricSummary>>([]);

        public Task<IReadOnlyList<MetricSessionInfo>> ListSessionAsync(
            int windowHours = 24, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetricSessionInfo>>([]);
    }

    private static IncomingMessage Msg(string text) => new()
    {
        Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "1" },
        Sender = new SenderInfo { UserId = "1", Nickname = "拉链" },
        Segments = [new TextSegment(text)],
        OccurredAt = DateTimeOffset.UtcNow,
    };

    private static ChatRef Chat => new() { Platform = "qq", Type = ChatType.Private, Id = "1" };

    [Fact]
    public async Task Batches_arriving_while_running_are_merged_into_the_same_turn()
    {
        var metrics = new FakeMetrics();
        var coordinator = new TurnCoordinator(
            new TurnOptions { SuspendedEnabled = false },
            metrics,
            new LoggerConfiguration().CreateLogger());

        var handled = new List<string>();
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var allDone = new TaskCompletionSource();

        coordinator.Submit(Chat, [Msg("第一")], async (batch, _) =>
        {
            lock (handled)
            {
                handled.Add(string.Join(",", batch.Select(m => m.PlainText)));
            }

            if (handled.Count == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            else if (handled.Count == 2)
            {
                allDone.TrySetResult();
            }
        }, CancellationToken.None);

        await firstStarted.Task;
        coordinator.Submit(Chat, [Msg("第二")], (_, _) => Task.CompletedTask, CancellationToken.None);
        releaseFirst.SetResult();

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["第一", "第二"], handled);
        Assert.Contains("turn.duration", metrics.Kinds);
    }

    [Fact]
    public async Task Handler_never_runs_concurrently_for_same_session()
    {
        var coordinator = new TurnCoordinator(
            new TurnOptions { SuspendedEnabled = false },
            new FakeMetrics(),
            new LoggerConfiguration().CreateLogger());

        var concurrent = 0;
        var maxConcurrent = 0;
        var done = new TaskCompletionSource();
        var handled = 0;

        for (var i = 0; i < 5; i++)
        {
            coordinator.Submit(Chat, [Msg($"m{i}")], async (batch, _) =>
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                await Task.Delay(10);
                Interlocked.Decrement(ref concurrent);

                // 批次会合并：按**消息条数**累计，而不是按调用次数
                if (Interlocked.Add(ref handled, batch.Count) >= 5)
                {
                    done.TrySetResult();
                }
            }, CancellationToken.None);
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task Suspend_timeout_ends_the_turn_and_next_message_starts_a_new_one()
    {
        var coordinator = new TurnCoordinator(
            new TurnOptions { SuspendedEnabled = true, SuspendTimeout = TimeSpan.FromMilliseconds(80) },
            new FakeMetrics(),
            new LoggerConfiguration().CreateLogger());

        var runs = 0;
        var firstDone = new TaskCompletionSource();

        coordinator.Submit(Chat, [Msg("a")], (_, _) =>
        {
            Interlocked.Increment(ref runs);
            firstDone.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);

        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 等挂起超时收轮，再发一条 → 必须是新的一轮（数量 +1）
        await Task.Delay(250);
        coordinator.Submit(Chat, [Msg("b")], (_, _) =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        }, CancellationToken.None);

        await Task.Delay(250);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Private_message_while_running_goes_to_steer_queue_not_new_batch()
    {
        var coordinator = new TurnCoordinator(
            new TurnOptions { SuspendedEnabled = false },
            new FakeMetrics(),
            new LoggerConfiguration().CreateLogger());

        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var calls = 0;

        coordinator.Submit(Chat, [Msg("先处理这条")], async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
        }, CancellationToken.None, preferSteer: true);

        await started.Task;

        coordinator.Submit(Chat, [Msg("再补一句")], (_, _) => Task.CompletedTask,
            CancellationToken.None, preferSteer: true);

        Assert.Equal(1, coordinator.SteerQueueFor(Chat.SessionId).Count);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.SetResult();
        await Task.Delay(100);

        // 收轮时没注入的引导消息会退回 pending，下一轮继续处理 —— 不静默丢
        Assert.Equal(0, coordinator.SteerQueueFor(Chat.SessionId).Count);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            Interlocked.CompareExchange(ref target, value, current);
        }
    }
}
