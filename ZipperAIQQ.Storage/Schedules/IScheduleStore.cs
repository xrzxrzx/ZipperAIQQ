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

    /// <summary>待执行任务总数（供状态查询展示）。</summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// 找「同一会话、到期时间落在 <paramref name="nextRunAt"/> ± <paramref name="tolerance"/> 内、
    /// 且重复方式相同」的**待执行**任务（创建幂等用，问题记录 04 #6）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只比**时间窗口 + 重复方式**，**不比正文** —— 实测证据（2026-11-05）：
    /// 库里成对的重复任务文案各不相同（#10「昨天说的那个事」/ #11「先查内核版本」），
    /// 但到期时间一模一样；#7/#8 只差 1 分钟。按 content 全等去重**拦不住**。
    /// </para>
    /// <para>
    /// 调用方仍需保留「用户确实要在同一时刻做两件不同的事」的出口（见 `schedule_create` 的
    /// <c>allowDuplicate</c>），否则「8 点吃药 + 8 点开会」会被误拦。
    /// </para>
    /// </remarks>
    Task<ScheduledTask?> FindSimilarAsync(
        string sessionId,
        DateTimeOffset nextRunAt,
        ScheduleRepeat repeat,
        int intervalMinutes,
        TimeSpan tolerance,
        CancellationToken ct = default);
}
