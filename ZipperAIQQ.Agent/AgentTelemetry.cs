using System.Diagnostics;

namespace ZipperAIQQ.Agent;

/// <summary>
/// Agent 侧的追踪埋点入口。
/// </summary>
/// <remarks>
/// <para>
/// 只定义 <see cref="ActivitySource"/>，**不依赖任何 OTel SDK** ——
/// 谁来收集、导出，由宿主（Server）决定。这样 Agent 项目保持轻量、可单测。
/// </para>
/// <para>
/// Span 层次：<c>agent.run</c> → <c>llm.call</c> / <c>tool.send_message</c>。
/// 目的是回答「这一轮 3 秒，时间花在模型上还是工具上」。
/// </para>
/// </remarks>
public static class AgentTelemetry
{
    public const string SourceName = "ZipperAIQQ.Agent";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}