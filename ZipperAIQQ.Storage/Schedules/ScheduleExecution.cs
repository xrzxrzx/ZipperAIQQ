namespace ZipperAIQQ.Storage.Schedules;

/// <summary>一次定时任务的执行结果（G-3 的审计依据）。</summary>
/// <param name="TaskId">任务 id。</param>
/// <param name="Outcome">Sent / SkippedByAgent / TimedOutDefaultSent / Failed。</param>
/// <param name="Detail">Agent 的理由或失败原因（可空）。</param>
/// <param name="CreatedAt">发生时间。</param>
public sealed record ScheduleExecution(
    long Id,
    long TaskId,
    string Outcome,
    string? Detail,
    DateTimeOffset CreatedAt);
