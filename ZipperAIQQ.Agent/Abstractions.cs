using ZipperAIQQ.Channel;
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

/// <summary>
/// <see cref="ChatAgent"/> 的全部依赖。
/// </summary>
/// <remarks>
/// <para>
/// 打包成记录不是"为了好看"：构造参数到 10 个之后，每加一个能力
/// 都要改构造签名 + 判空 + 赋值 + 所有调用点，四份重复劳动。
/// </para>
/// <para>
/// 现在加能力只改这一个类型定义，调用点用对象初始化器即可。
/// </para>
/// </remarks>
public sealed record ChatAgentDependencies
{
    public required Microsoft.Extensions.AI.IChatClient Client { get; init; }
    public required IMessageChannel Channel { get; init; }
    public required Storage.Messages.IMessageStore Store { get; init; }
    public required Faces.FaceCatalog Faces { get; init; }
    public required Memory.MemoryRetriever Retriever { get; init; }
    public required Memory.SummaryService Summaries { get; init; }
    public required Storage.Summaries.ISummaryStore SummaryStore { get; init; }
    public required Storage.Personas.IPersonaStore Personas { get; init; }
    public required Storage.Metrics.IMetricsStore Metrics { get; init; }

    /// <summary>
    /// 事件总线：漂移 / 降级等 Agent 事件走这里（§8.3）。
    /// </summary>
    public required Core.Events.IEventBus Events { get; init; }

    /// <summary>权限门禁（接缝 C，FR-6）。</summary>
    public required Security.IPermissionGate PermissionGate { get; init; }

    /// <summary>角色解析（管理员 / 工具白名单 / 普通）。</summary>
    public required Security.IRoleResolver Roles { get; init; }

    /// <summary>会话工作区（路径穿越拦截，FR-7）。</summary>
    public required Security.WorkspacePathResolver Workspace { get; init; }

    /// <summary>单次读写大小上限（字节，FR-7.7）。</summary>
    public int FileMaxBytes { get; init; } = 256 * 1024;

    /// <summary>定时任务仓储（FR-14）。</summary>
    public required Storage.Schedules.IScheduleStore Schedules { get; init; }

    public required ChatAgentOptions Options { get; init; }
    public required Serilog.ILogger Logger { get; init; }
}