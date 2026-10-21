using ZipperAIQQ.Agent.Llm;

namespace ZipperAIQQ.Agent;

/// <summary>Agent 的运行时选项。</summary>
public sealed record ChatAgentOptions
{
    /// <summary>
    /// 人设缺失时的兜底 system 文本。
    /// </summary>
    /// <remarks>
    /// M1-D 起人设改由 <see cref="Storage.Personas.IPersonaStore"/> 提供（会话级可覆盖）。
    /// 这里只在**全局人设文件也读不到**时兜底，保证不会出现"没有 system"的空壳对话。
    /// </remarks>
    public string? FallbackSystemPrompt { get; init; }

    /// <summary>单次 send_message 最多发几条（FR-19.2）。</summary>
    public int MaxMessagesPerCall { get; init; } = 5;

    /// <summary>
    /// 两条消息之间的**最小发送间隔（毫秒）**。
    /// </summary>
    /// <remarks>
    /// 目的不是"慢"，而是**防风控**：短时间连发多条容易被判定为刷屏。
    /// 默认 0（不限速）—— 因为实测 NapCat 单条往返只要 ~140ms，
    /// 而"等 echo"本身已经天然形成了间隔。
    /// </remarks>
    public int MinSendIntervalMs { get; init; } = 0;

    /// <summary>上下文里保留多少条历史消息（热窗，FR-3）。</summary>
    public int HotWindowMessages { get; init; } = 20;
}