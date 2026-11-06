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
/// <summary>JEV 判定结果（K-3：一次调用同时回答「要不要回」和「引导还是排队」）。</summary>
public sealed record JevVerdict(bool ShouldReply, bool PreferSteer);

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

    /// <summary>
    /// 一次判定两件事：要不要回、如果会话正忙是否需要**引导**当前轮（否则排队）。
    /// </summary>
    /// <remarks>
    /// 默认实现退化为「只判要不要回、不引导」——旧实现零改动；
    /// Server 侧实现复用**同一次** judge 调用同时取两个答案，不额外发请求。
    /// </remarks>
    async Task<JevVerdict> DecideAsync(
        Channel.Models.IncomingMessage message,
        string selfId,
        CancellationToken ct = default) =>
        new(await ShouldReplyAsync(message, selfId, ct).ConfigureAwait(false), false);
}
