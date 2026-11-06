namespace ZipperAIQQ.Storage.Turns;

/// <summary>一轮的审计记录（K-5：静默收轮的唯一产物）。</summary>
public sealed record TurnLog(
    long Id,
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string EndReason,
    int SteerCount,
    int FollowUpCount);
