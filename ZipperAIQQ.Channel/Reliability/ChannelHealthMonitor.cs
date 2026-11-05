using ZipperAIQQ.Channel.Failures;

namespace ZipperAIQQ.Channel.Reliability;

/// <summary>
/// 单个通道的健康状态机：连续失败 → 熔断 → 冷却后半开探测。
/// </summary>
/// <remarks>
/// <para>
/// 平台无关：QQ / 抖音 / 以后的平台都挂这一个。**恢复动作**不在这里（见 <see cref="IRecoverableChannel"/>），
/// 这里只管「什么时候该停下来」。
/// </para>
/// <para>
/// 只有 <see cref="FailureKind.ChannelUnavailable"/> 计入「通道坏了」。
/// 发送超时、目标非法、内容被拒都不应触发熔断 —— 那些是单条消息的问题。
/// </para>
/// </remarks>
public sealed class ChannelHealthMonitor
{
    private readonly object _gate = new();
    private readonly int _openAfterFailures;
    private readonly TimeSpan _openCooldown;

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset? _openedAt;

    public ChannelHealthMonitor(
        string platform,
        int openAfterFailures = 3,
        TimeSpan? openCooldown = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(openAfterFailures);

        Platform = platform;
        _openAfterFailures = openAfterFailures;
        _openCooldown = openCooldown ?? TimeSpan.FromSeconds(30);
    }

    public string Platform { get; }

    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveFailures;
            }
        }
    }

    public DateTimeOffset? OpenedAt
    {
        get
        {
            lock (_gate)
            {
                return _openedAt;
            }
        }
    }

    /// <summary>通道是否处于熔断（决定要不要停手、要不要跳过模型调用）。</summary>
    public bool IsChannelDown
    {
        get
        {
            lock (_gate)
            {
                return _state == CircuitState.Open;
            }
        }
    }

    /// <summary>记一次成功：清零并合闸。</summary>
    /// <returns>状态是否发生变化（供上层决定要不要发事件）。</returns>
    public bool RecordSuccess()
    {
        lock (_gate)
        {
            var changed = _state != CircuitState.Closed || _consecutiveFailures > 0;
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
            _openedAt = null;
            return changed;
        }
    }

    /// <summary>记一次失败；只有「通道不可用」计入熔断计数。</summary>
    /// <returns>本次是否**刚好**打开熔断（用于只报一次警）。</returns>
    public bool RecordFailure(FailureKind kind, DateTimeOffset now)
    {
        if (!SendFailureClassifier.IsChannelDown(kind))
        {
            return false;
        }

        lock (_gate)
        {
            _consecutiveFailures++;

            if (_state == CircuitState.HalfOpen)
            {
                // 探测失败：立刻回到熔断，重新计时
                _state = CircuitState.Open;
                _openedAt = now;
                return true;
            }

            if (_state == CircuitState.Closed && _consecutiveFailures >= _openAfterFailures)
            {
                _state = CircuitState.Open;
                _openedAt = now;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 冷却结束后进入半开：允许放一次探测。
    /// </summary>
    /// <returns>是否刚进入半开（调用方据此发起一次恢复尝试）。</returns>
    public bool TryHalfOpen(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_state != CircuitState.Open || _openedAt is null)
            {
                return false;
            }

            if (now - _openedAt.Value < _openCooldown)
            {
                return false;
            }

            _state = CircuitState.HalfOpen;
            return true;
        }
    }
}
