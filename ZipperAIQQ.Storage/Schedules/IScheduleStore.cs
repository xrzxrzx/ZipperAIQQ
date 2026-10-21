using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Storage.Schedules;

/// <summary>任务的重复方式。</summary>
public enum ScheduleRepeat
{
    /// <summary>只执行一次，执行后结束。</summary>
    Once = 0,

    /// <summary>按固定间隔重复。</summary>
    Interval = 1,
}

/// <summary>任务状态。</summary>
public enum ScheduleStatus
{
    Pending = 0,
    Done = 1,
    Cancelled = 2,
}

/// <summary>
/// 一条定时任务。
/// </summary>
/// <param name="MentionUserId">群聊里要 @ 的人（可空）。</param>
/// <param name="IntervalMinutes">周期任务的间隔分钟数；一次性任务为 0。</param>
public sealed record ScheduledTask(
    long Id,
    ChatRef Chat,
    string Content,
    string? MentionUserId,
    ScheduleRepeat Repeat,
    int IntervalMinutes,
    DateTimeOffset NextRunAt,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    ScheduleStatus Status);

/// <summary>
/// 定时任务仓储（FR-14）。
/// </summary>
/// <remarks>
/// <para>
/// <b>数据库即队列</b>：不另建内存队列 —— 那会引入「DB 与内存两份真相」，
/// 这个项目已经在这上面吃过两次亏。单实例部署下，带索引的到期查询 &lt; 1ms。
/// </para>
/// <para>
/// 时间一律**存 UTC 毫秒**，只在显示/注入时转北京时间（FR-15.3）。
/// </para>
/// </remarks>
public interface IScheduleStore
{
    /// <summary>新建任务，返回主键。</summary>
    Task<long> CreateAsync(ScheduledTask task, CancellationToken ct = default);

    /// <summary>取已到期、待执行的任务（按到期时间正序）。</summary>
    Task<IReadOnlyList<ScheduledTask>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>列出某会话的任务（含已完成/已取消，供客户端展示）。</summary>
    Task<IReadOnlyList<ScheduledTask>> ListAsync(string sessionId, CancellationToken ct = default);

    /// <summary>取消任务（只能取消本会话的、且仍待执行的）。</summary>
    /// <returns>是否真的取消了一条。</returns>
    Task<bool> CancelAsync(long id, string sessionId, CancellationToken ct = default);

    /// <summary>标记一次性任务已完成。</summary>
    Task MarkDoneAsync(long id, CancellationToken ct = default);

    /// <summary>把周期任务推到下一次执行时间。</summary>
    Task RescheduleAsync(long id, DateTimeOffset nextRunAt, CancellationToken ct = default);
}
