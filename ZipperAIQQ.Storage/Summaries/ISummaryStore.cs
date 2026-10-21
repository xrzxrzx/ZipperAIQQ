namespace ZipperAIQQ.Storage.Summaries;

/// <summary>一个会话的摘要快照。</summary>
/// <param name="CoversUpToId">已压缩到的消息主键（含）。之后的还没压。</param>
public sealed record SessionSummary(
    string SessionId,
    string Summary,
    long CoversUpToId,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 会话摘要的存储（M1-C 温层）。
/// </summary>
/// <remarks>
/// 只保留**最新一份**摘要（每个会话一行）：压缩是「在上一版摘要基础上继续压」，
/// 不需要历史版本。真要追溯原始对话，冷层的消息表还在。
/// </remarks>
public interface ISummaryStore
{
    Task<SessionSummary?> GetAsync(string sessionId, CancellationToken ct = default);

    Task SaveAsync(
        string sessionId,
        string summary,
        long coversUpToId,
        CancellationToken ct = default);
}
