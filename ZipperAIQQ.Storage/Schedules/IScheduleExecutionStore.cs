namespace ZipperAIQQ.Storage.Schedules;

/// <summary>定时任务执行审计（谁决定发 / 不发 / 失败）。</summary>
public interface IScheduleExecutionStore
{
    Task AppendAsync(ScheduleExecution execution, CancellationToken ct = default);

    /// <summary>全局最近的执行记录（新的在前，供客户端展示）。</summary>
    Task<IReadOnlyList<ScheduleExecution>> ListRecentAsync(
        int limit = 20, CancellationToken ct = default);

    /// <summary>某个任务最近的执行记录（新的在前）。</summary>
    Task<IReadOnlyList<ScheduleExecution>> ListByTaskAsync(
        long taskId, int limit = 20, CancellationToken ct = default);
}
