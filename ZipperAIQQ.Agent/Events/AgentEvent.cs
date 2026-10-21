namespace ZipperAIQQ.Agent.Events;

/// <summary>
/// Agent 运行时事件（M2 客户端将通过 WS 订阅）。
/// </summary>
/// <remarks>
/// <para>
/// 现在只有服务端日志在消费；客户端（M2）接上 <c>WS /events</c> 后
/// 直接订阅同一类事件即可，**发布点不需要改**。
/// </para>
/// <para>
/// 事件只带元数据（会话、类别、计数），<b>不带消息正文</b> —— 正文是敏感内容。
/// </para>
/// </remarks>
public abstract record AgentEvent(string SessionId, DateTimeOffset OccurredAt);

/// <summary>
/// 检测到「指令遵循漂移」：该调工具却没调（§8.3）。
/// </summary>
/// <param name="Kind">漂移类别，如 <c>instruction</c>。</param>
/// <param name="Detail">可判定的具体信号，如 <c>no_tool_call</c>。</param>
public sealed record DriftDetectedEvent(
    string SessionId,
    DateTimeOffset OccurredAt,
    string Kind,
    string Detail)
    : AgentEvent(SessionId, OccurredAt);

/// <summary>
/// 降级发送：连续 2 次没调工具，由我们代发（§8.3）。
/// </summary>
/// <param name="SentCount">代发成功的条数。</param>
public sealed record SendDegradedEvent(
    string SessionId,
    DateTimeOffset OccurredAt,
    int SentCount)
    : AgentEvent(SessionId, OccurredAt);
