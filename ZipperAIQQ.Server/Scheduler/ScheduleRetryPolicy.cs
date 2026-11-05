namespace ZipperAIQQ.Server.Scheduler;

/// <summary>
/// 定时任务发送失败的重试策略。
/// </summary>
/// <remarks>
/// <para>
/// 旧行为是「不推进时间 → 下一轮（1 分钟后）再试」，等于**无限重试**：
/// 11-02 那两条任务因此每分钟失败一次、连续跑了四天（问题记录 04 #5 / #6）。
/// </para>
/// <para>
/// 现在：指数退避（1/2/4/8 分钟，封顶 10）+ 次数上限 10。到上限后把任务标记为
/// <see cref="Storage.Schedules.ScheduleStatus.Failed"/> 并发告警 ——
/// **明确停手**比无声地烧日志和额度强。
/// </para>
/// </remarks>
public static class ScheduleRetryPolicy
{
    /// <summary>最多尝试几次（含首次）。</summary>
    public const int MaxAttempts = 10;

    /// <summary>第 <paramref name="attempts"/> 次失败后，等多久再试。</summary>
    public static TimeSpan DelayFor(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempts);

        var minutes = Math.Min(1 << Math.Min(attempts - 1, 10), 10);
        return TimeSpan.FromMinutes(minutes);
    }
}
