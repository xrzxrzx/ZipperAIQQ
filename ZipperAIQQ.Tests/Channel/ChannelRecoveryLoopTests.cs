using Serilog;
using ZipperAIQQ.Channel.Reliability;

namespace ZipperAIQQ.Tests.Channel;

/// <summary>
/// 共用恢复循环：所有平台同一套「断开 → 退避 → 恢复」。
/// </summary>
/// <remarks>
/// 时钟与等待都是注入的 —— 测试不睡真实时间，也能断言「第几次等多久」。
/// </remarks>
public sealed class ChannelRecoveryLoopTests
{
    private sealed class FakeChannel : IRecoverableChannel
    {
        public string Platform => "qq";

        public bool Connected { get; set; }

        public int Attempts { get; private set; }

        /// <summary>第 n 次（1 起）尝试是否成功。</summary>
        public Func<int, bool> Recover { get; set; } = _ => false;

        public bool IsConnected => Connected;

        public Task<bool> TryRecoverAsync(CancellationToken ct)
        {
            Attempts++;
            Connected = Recover(Attempts);
            return Task.FromResult(Connected);
        }
    }

    private sealed class Harness
    {
        public FakeChannel Channel { get; } = new();
        public ChannelHealthMonitor Monitor { get; } = new("qq", openAfterFailures: 3, openCooldown: TimeSpan.FromSeconds(30));
        public List<TimeSpan> Delays { get; } = [];
        public DateTimeOffset Now { get; private set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8));
        public CancellationTokenSource Cts { get; } = new();
        public int CancelAfterDelays { get; set; } = 6;

        public ChannelRecoveryLoop Loop => new(
            Channel,
            Monitor,
            new ReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)),
            new LoggerConfiguration().CreateLogger())
        {
            PollInterval = TimeSpan.FromSeconds(5),
            Clock = () => Now,
            Delay = (delay, ct) =>
            {
                Delays.Add(delay);
                Now += delay;
                if (Delays.Count >= CancelAfterDelays)
                {
                    Cts.Cancel();
                }

                return Task.CompletedTask;
            },
        };
    }

    [Fact]
    public async Task Already_connected_does_not_try_to_recover()
    {
        var harness = new Harness();
        harness.Channel.Connected = true;
        harness.CancelAfterDelays = 1;

        await harness.Loop.RunAsync(harness.Cts.Token);

        Assert.Equal(0, harness.Channel.Attempts);
        Assert.Equal(CircuitState.Closed, harness.Monitor.State);
    }

    [Fact]
    public async Task Recovers_on_second_attempt_and_resets_the_counter()
    {
        var harness = new Harness();
        harness.Channel.Recover = attempt => attempt >= 2;
        harness.CancelAfterDelays = 3;

        await harness.Loop.RunAsync(harness.Cts.Token);

        Assert.Equal(2, harness.Channel.Attempts);
        Assert.True(harness.Channel.IsConnected);
        Assert.Equal(CircuitState.Closed, harness.Monitor.State);
        Assert.Equal(0, harness.Monitor.ConsecutiveFailures);
    }

    [Fact]
    public async Task Recovery_records_downtime_and_count()
    {
        var harness = new Harness();
        harness.Channel.Recover = attempt => attempt >= 2;
        harness.CancelAfterDelays = 3;

        await harness.Loop.RunAsync(harness.Cts.Token);

        Assert.Equal(1, harness.Monitor.DisconnectCount);
        Assert.NotNull(harness.Monitor.LastDowntime);
        Assert.True(harness.Monitor.LastDowntime > TimeSpan.Zero);
    }

    [Fact]
    public async Task Repeated_failures_back_off_then_open_the_circuit()
    {
        var harness = new Harness();
        harness.CancelAfterDelays = 4;

        await harness.Loop.RunAsync(harness.Cts.Token);

        // 1s → 2s → 4s（指数退避），第 3 次失败达到阈值后熔断
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)],
            harness.Delays);
        Assert.True(harness.Monitor.IsChannelDown);
        Assert.Equal(3, harness.Monitor.ConsecutiveFailures);
    }

    [Fact]
    public async Task Circuit_open_waits_for_cooldown_without_hammering()
    {
        var harness = new Harness();
        harness.CancelAfterDelays = 12;

        await harness.Loop.RunAsync(harness.Cts.Token);

        // 熔断后不再立刻重试：尝试次数应该被冷却期挡住，明显少于延迟次数
        Assert.True(harness.Channel.Attempts <= 5, $"实际尝试 {harness.Channel.Attempts} 次");
        Assert.True(harness.Monitor.IsChannelDown);
    }
}
