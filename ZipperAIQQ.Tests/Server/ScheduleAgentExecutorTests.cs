using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Channel.Failures;
using ZipperAIQQ.Server.Channels;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Scheduler;
using ZipperAIQQ.Storage.Schedules;

namespace ZipperAIQQ.Tests.Server;

/// <summary>定时任务投 Agent 的三种结局（G-2 / G-3）。</summary>
public sealed class ScheduleAgentExecutorTests
{
    private sealed class FakeAgent : IChatAgent
    {
        public string SessionId => "qq:p1";

        public Func<AgentRunResult>? Result { get; set; }

        public bool WaitForever { get; set; }

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
        {
            if (WaitForever)
            {
                return Task.Delay(Timeout.Infinite, ct).ContinueWith<AgentRunResult>(
                    _ => throw new OperationCanceledException(ct), ct);
            }

            return Task.FromResult(Result?.Invoke() ?? new AgentRunResult(null, 0));
        }

        public void ResetHistory()
        {
        }

        public void RememberCommand(string userMessage, string commandResponse)
        {
        }
    }

    private sealed class FakePool(IChatAgent agent) : IChatAgentPool
    {
        public IChatAgent GetOrCreate(ChatRef chat) => agent;
        public int LoadedCount => 1;
    }

    private sealed class FakeChannel : IMessageChannel
    {
        public List<OutgoingMessage> Sent { get; } = [];

        public string Platform => "qq";
        public ChannelCapabilities Capabilities => ChannelCapabilities.Text;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(OutgoingMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeExecutions : IScheduleExecutionStore
    {
        public List<ScheduleExecution> Rows { get; } = [];

        public Task AppendAsync(ScheduleExecution execution, CancellationToken ct = default)
        {
            Rows.Add(execution);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScheduleExecution>> ListRecentAsync(
            int limit = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduleExecution>>(Rows);

        public Task<IReadOnlyList<ScheduleExecution>> ListByTaskAsync(
            long taskId, int limit = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduleExecution>>(Rows);
    }

    private static readonly ScheduledTask SampleTask = new(
        Id: 7,
        Chat: new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "10001" },
        Content: "该睡觉了",
        MentionUserId: null,
        Repeat: ScheduleRepeat.Once,
        IntervalMinutes: 0,
        NextRunAt: DateTimeOffset.UtcNow,
        CreatedAt: DateTimeOffset.UtcNow.AddHours(-1),
        CreatedBy: "10001",
        Status: ScheduleStatus.Pending);

    private static (ScheduleAgentExecutor Executor, FakeChannel Channel, FakeExecutions Executions) Create(
        FakeAgent agent, int timeoutSeconds = 60, ChannelHealthRegistry? health = null)
    {
        var channel = new FakeChannel();
        var executions = new FakeExecutions();
        var executor = new ScheduleAgentExecutor(
            new FakePool(agent),
            channel,
            executions,
            new LoggerConfiguration().CreateLogger(),
            health)
        {
            DecisionTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        return (executor, channel, executions);
    }

    [Fact]
    public async Task Channel_circuit_open_skips_without_calling_agent()
    {
        var health = new ChannelHealthRegistry(new ChannelReliabilityOptions
        {
            OpenAfterFailures = 1,
            OpenCooldownSeconds = 300,
        });
        health.Get("qq").RecordFailure(FailureKind.ChannelUnavailable, DateTimeOffset.UtcNow);

        var agent = new FakeAgent { Result = () => new AgentRunResult("不该被调用", 1) };
        var (executor, channel, executions) = Create(agent, health: health);

        var result = await executor.ExecuteAsync(SampleTask, CancellationToken.None);

        Assert.Equal(ScheduleExecutionOutcome.ChannelDownSkipped, result.Outcome);
        Assert.Empty(channel.Sent);
        Assert.Equal("ChannelDownSkipped", Assert.Single(executions.Rows).Outcome);
    }

    [Fact]
    public async Task Agent_sent_means_sent()
    {
        var agent = new FakeAgent { Result = () => new AgentRunResult("发了", 2) };
        var (executor, _, executions) = Create(agent);

        var result = await executor.ExecuteAsync(SampleTask, CancellationToken.None);

        Assert.Equal(ScheduleExecutionOutcome.Sent, result.Outcome);
        Assert.Equal("Sent", Assert.Single(executions.Rows).Outcome);
    }

    [Fact]
    public async Task Agent_silence_is_recorded_as_skipped()
    {
        var agent = new FakeAgent { Result = () => new AgentRunResult("SKIP", 0) };
        var (executor, channel, executions) = Create(agent);

        var result = await executor.ExecuteAsync(SampleTask, CancellationToken.None);

        Assert.Equal(ScheduleExecutionOutcome.SkippedByAgent, result.Outcome);
        Assert.Contains("SKIP", result.Detail);
        Assert.Empty(channel.Sent);
        Assert.Equal("SkippedByAgent", Assert.Single(executions.Rows).Outcome);
    }

    [Fact]
    public async Task Timeout_falls_back_to_sending_the_original_content()
    {
        var agent = new FakeAgent { WaitForever = true };
        var (executor, channel, executions) = Create(agent, timeoutSeconds: 1);

        var result = await executor.ExecuteAsync(SampleTask, CancellationToken.None);

        Assert.Equal(ScheduleExecutionOutcome.TimedOutDefaultSent, result.Outcome);
        Assert.Single(channel.Sent);
        Assert.Equal("TimedOutDefaultSent", Assert.Single(executions.Rows).Outcome);
    }
}
