namespace ZipperAIQQ.Agent;

/// <summary>
/// 会话级发送节流：保证**同一会话**两次真正发出之间至少间隔 N 毫秒。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不是"工具内部多条之间等"：那样只在一次 <c>send_message</c> 发多条时有用，
/// **单条发送、连续两轮对话之间完全没有间隔** —— 而这恰恰是最容易触发平台风控的形态。
/// </para>
/// <para>
/// 一个会话一个 <see cref="ChatAgent"/> 实例，所以状态放实例上就够（不必全局字典）。
/// </para>
/// </remarks>
public sealed class SendThrottle
{
    private readonly object _gate = new();
    private long _lastSentTicks;

    /// <summary>等到距上次发送至少 <paramref name="intervalMs"/> 毫秒；然后记账。</summary>
    public async Task WaitAsync(int intervalMs, CancellationToken ct = default)
    {
        if (intervalMs <= 0)
        {
            return;
        }

        TimeSpan delay;

        lock (_gate)
        {
            var now = Environment.TickCount64;
            var sinceLast = _lastSentTicks == 0 ? long.MaxValue : now - _lastSentTicks;
            delay = sinceLast >= intervalMs
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(intervalMs - sinceLast);

            // 记账用"现在 + 需要等待的时间"，这样并发调用者也会按顺序排开
            _lastSentTicks = now + (long)delay.TotalMilliseconds;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }
}
