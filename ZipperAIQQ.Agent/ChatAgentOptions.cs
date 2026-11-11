using ZipperAIQQ.Agent.Llm;

namespace ZipperAIQQ.Agent;

/// <summary>Agent 的运行时选项。</summary>
public sealed record ChatAgentOptions
{
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

    /// <summary>机器人自身账号（配置兜底；运行时优先用通道动态 SelfId）。</summary>
    public string? SelfId { get; init; }

    /// <summary>上下文里保留多少条历史消息（热窗，FR-3）。</summary>
    public int HotWindowMessages { get; init; } = 20;

    /// <summary>
    /// 单次模型调用上限（秒）。超过就中止本轮，避免一轮跑 5~20 分钟。
    /// </summary>
    /// <remarks>
    /// 2026-11-11 事故：指标里 turn.duration 平均 302 秒、最长 1316 秒，
    /// 线程池被长调用拖到饥饿，HTTP 全部变慢。给每次调用加硬上限是最直接的止血。
    /// </remarks>
    public int LlmTimeoutSeconds { get; init; } = 180;
}