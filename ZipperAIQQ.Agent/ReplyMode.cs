namespace ZipperAIQQ.Agent;

/// <summary>回复触发模式（FR-11）。</summary>
public enum ReplyMode
{
    /// <summary>每条消息都回（私聊 / 小群）。</summary>
    Every = 0,

    /// <summary>只被 <c>@</c> 或命中唤醒词时回（大群，避免刷屏）。</summary>
    Mention = 1,

    /// <summary>先由判断模型决定"该不该回"，命中才触发（自然插话）。</summary>
    Jev = 2,
}

/// <summary>
/// 判断一条消息是否该触发回复（FR-11）。
/// </summary>
/// <remarks>
/// 命令**不经过**这里 —— <c>/help</c> 这类永远该响应，不受回复模式影响。
/// </remarks>
public interface IReplyModeDecider
{
    /// <summary>当前生效模式（全局默认 + 每聊天覆盖）。</summary>
    Task<ReplyMode> GetModeAsync(string sessionId, CancellationToken ct = default);

    /// <summary>这条消息要不要回。</summary>
    Task<bool> ShouldReplyAsync(
        Channel.Models.IncomingMessage message,
        string selfId,
        CancellationToken ct = default);

    /// <summary>
    /// 这条消息是否 @ 了机器人（群聊里命令也靠它把关）。
    /// </summary>
    /// <remarks>
    /// 没配 <c>ZIPPER_SELF_ID</c> 时：群聊一律视为"没 @ 到"（无法判断），私聊视为 true。
    /// </remarks>
    bool IsMentioned(Channel.Models.IncomingMessage message, string selfId);
}
