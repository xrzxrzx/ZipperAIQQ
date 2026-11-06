using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Cost;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Schedules;

namespace ZipperAIQQ.Server.Scheduler;

/// <summary>一次定时任务的执行结论。</summary>
public enum ScheduleExecutionOutcome
{
    /// <summary>Agent 发了至少一条。</summary>
    Sent = 0,

    /// <summary>Agent 决定不发（必须留审计理由）。</summary>
    SkippedByAgent = 1,

    /// <summary>Agent 超时没决策，按兜底模板发了原内容。</summary>
    TimedOutDefaultSent = 2,

    /// <summary>执行失败（由轮询器走重试/上限）。</summary>
    Failed = 3,
}

public sealed record ScheduleExecutionResult(ScheduleExecutionOutcome Outcome, string? Detail);

/// <summary>执行一次定时任务（G-2）。</summary>
public interface IScheduleExecutor
{
    Task<ScheduleExecutionResult> ExecuteAsync(ScheduledTask task, CancellationToken ct);
}

/// <summary>
/// 把定时任务投给**目标会话的 Agent**，由它决定发什么 / 要不要发（D-08）。
/// </summary>
/// <remarks>
/// <para>
/// 旧实现到点直接 <c>channel.SendAsync</c>：没有上下文、不会改写、失败也没人管。
/// 现在交给人格 agent；代价是**每个任务一次模型调用**（用户已拍板接受）。
/// </para>
/// <para>
/// 两道保险：① <see cref="ScheduleExecutionScope"/> 期间禁止再建定时任务（防套娃）；
/// ② 超时（默认 60s）没决策就按原内容兜底发一条 —— 定了时却完全没动静比发得笨更糟。
/// </para>
/// </remarks>
public sealed class ScheduleAgentExecutor : IScheduleExecutor
{
    private readonly IChatAgentPool _agents;
    private readonly IMessageChannel _channel;
    private readonly IScheduleExecutionStore _executions;
    private readonly ILogger _logger;

    public ScheduleAgentExecutor(
        IChatAgentPool agents,
        IMessageChannel channel,
        IScheduleExecutionStore executions,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(logger);

        _agents = agents;
        _channel = channel;
        _executions = executions;
        _logger = logger;
    }

    /// <summary>等 Agent 决策的上限。</summary>
    public TimeSpan DecisionTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<ScheduleExecutionResult> ExecuteAsync(ScheduledTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);

        using var scheduleScope = ScheduleExecutionScope.Begin();
        using var purposeScope = CallPurposeScope.Begin(CallPurpose.SchedulePolish);

        var instruction =
            $"【定时任务 #{task.Id} 到点】这是你之前给自己/主人的提醒，原内容如下：\n" +
            $"「{task.Content}」\n" +
            "请结合当前对话与人设，决定要不要发、发什么、分几条发（用 send_message）。" +
            "如果确实已经没有提醒的必要（例如对方刚刚已经做过），就只输出 SKIP，不要发消息。";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DecisionTimeout);

        try
        {
            var result = await _agents
                .GetOrCreate(task.Chat)
                .RunAsync(
                    new AgentRunRequest
                    {
                        Chat = task.Chat,
                        Sender = new SenderInfo { UserId = ChatAgent.BotUserId },
                        UserText = instruction,
                        Segments = [new TextSegment(instruction)],
                        Now = BeijingTime.Now,
                    },
                    timeoutCts.Token)
                .ConfigureAwait(false);

            if (result.MessagesSent > 0)
            {
                return await RecordAsync(task, ScheduleExecutionOutcome.Sent, null, ct).ConfigureAwait(false);
            }

            // 没发：可能是模型输出 SKIP，也可能是漂移兜底也失败。都按「决定不发」记，理由留正文。
            var reason = string.IsNullOrWhiteSpace(result.FinalText) ? "Agent 未发送（无正文）" : Truncate(result.FinalText, 200);
            return await RecordAsync(task, ScheduleExecutionOutcome.SkippedByAgent, reason, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.Warning("定时任务 #{Id} 的 Agent 决策超时（{Timeout}s），按原内容兜底发送",
                task.Id, DecisionTimeout.TotalSeconds);

            await _channel.SendAsync(
                new OutgoingMessage
                {
                    Chat = task.Chat,
                    Segments = BuildSegments(task),
                },
                ct).ConfigureAwait(false);

            return await RecordAsync(task, ScheduleExecutionOutcome.TimedOutDefaultSent,
                $"超时 {DecisionTimeout.TotalSeconds:F0}s，已按原内容发送", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RecordAsync(task, ScheduleExecutionOutcome.Failed, ex.Message, CancellationToken.None)
                .ConfigureAwait(false);
            throw;   // 交给轮询器的重试/上限逻辑
        }
    }

    private async Task<ScheduleExecutionResult> RecordAsync(
        ScheduledTask task,
        ScheduleExecutionOutcome outcome,
        string? detail,
        CancellationToken ct)
    {
        await _executions.AppendAsync(
            new ScheduleExecution(0, task.Id, outcome.ToString(), detail, BeijingTime.Now), ct)
            .ConfigureAwait(false);

        return new ScheduleExecutionResult(outcome, detail);
    }

    private static IReadOnlyList<MessageSegment> BuildSegments(ScheduledTask task)
    {
        var segments = new List<MessageSegment> { new TextSegment(task.Content) };

        if (!string.IsNullOrWhiteSpace(task.MentionUserId))
        {
            segments.Add(new MentionSegment(task.MentionUserId));
        }

        return segments;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");
}
