using ZipperAIQQ.Agent.Llm;

namespace ZipperAIQQ.Agent;

/// <summary>Agent 的运行时选项（M1-B 最小集；人设/预取等在 M1-C/D 扩展）。</summary>
public sealed record ChatAgentOptions
{
    /// <summary>system 提示（M1-B 先用固定文本，M1-D 换成可配置人设）。</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>单次 send_message 最多发几条（FR-19.2）。</summary>
    public int MaxMessagesPerCall { get; init; } = 5;

    /// <summary>上下文里保留多少条历史消息（热窗，FR-3）。</summary>
    public int HotWindowMessages { get; init; } = 20;
}