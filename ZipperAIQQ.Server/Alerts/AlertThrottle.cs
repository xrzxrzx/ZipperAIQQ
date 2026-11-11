namespace ZipperAIQQ.Server.Alerts;

/// <summary>
/// 通道告警节流（按「通道 + 状态」），避免熔断抖动时每 30 秒刷一条。
/// </summary>
/// <remarks>
/// 与 <see cref="OwnerAlertService"/> 的库内抑制是**两层**：
/// 库内抑制保证跨重启不重复；这里在内存里先挡一次，避免抖动期间频繁读库。
/// </remarks>
public sealed class AlertThrottle
{
    private readonly Dictionary<string, DateTimeOffset> _lastSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _clock;

    public AlertThrottle(TimeSpan window, Func<DateTimeOffset>? clock = null)
    {
        _window = window;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>该 key 现在能不能发；能发就记下时间。</summary>
    public bool TryAcquire(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var now = _clock();
        lock (_gate)
        {
            if (_lastSent.TryGetValue(key, out var last) && now - last < _window)
            {
                return false;
            }

            _lastSent[key] = now;
            return true;
        }
    }
}
