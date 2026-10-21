using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Agent;

/// <summary>一轮对话的输入。</summary>
public sealed record AgentRunRequest
{
    public required ChatRef Chat { get; init; }
    public required SenderInfo Sender { get; init; }
    public required string UserText { get; init; }

    /// <summary>
    /// 原始消息片段（含表情/图片等）。
    /// </summary>
    /// <remarks>
    /// UserText 只是纯文本，**看不到表情**；渲染上下文时要看这个。
    /// </remarks>
    public IReadOnlyList<MessageSegment> Segments { get; init; } = [];

    /// <summary>
    /// 同一批合并进来的多条消息（数量 &gt; 1 表示短时间内积压）。
    /// </summary>
    /// <remarks>
    /// 逐条触发会让 10 条消息变成 10 次模型调用；合并成一批只调一次，
    /// 而且更符合真人的反应（看到连发几条，一起回）。
    /// </remarks>
    public IReadOnlyList<IncomingMessage> Batch { get; init; } = [];
    public required DateTimeOffset Now { get; init; }
}

/// <summary>一轮对话的结果。</summary>
/// <param name="FinalText">模型产出的最终文本（可能为 null）。</param>
/// <param name="MessagesSent">本轮通过工具实际发出的条数（用于兜底判断）。</param>
public sealed record AgentRunResult(string? FinalText, int MessagesSent);

/// <summary>
/// 一个聊天一个实例的执行体（需求 D9）。
/// </summary>
/// <remarks>同一实例内部**串行**执行；不同会话并行。</remarks>
public interface IChatAgent
{
    string SessionId { get; }

    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default);
}

/// <summary>按会话取/建 agent 实例。</summary>
public interface IChatAgentPool
{
    IChatAgent GetOrCreate(ChatRef chat);
    int LoadedCount { get; }
}