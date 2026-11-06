namespace ZipperAIQQ.Server.Reply;

/// <summary>
/// JEV 判定结果缓存（K-3 前置复用）。
/// </summary>
/// <remarks>
/// <para>
/// 现状：群聊里**每条消息**都会打一次 judge；连发 10 条 = 10 次调用，
/// 而同一批消息最终只会合并成一轮回复 —— 判定被白做。
/// </para>
/// <para>
/// 这里按「会话 + 消息标识」缓存判定结果（默认 10 分钟）：
/// 重放、同一消息被多条路径检查、以及 K-3 的「引导/排队」判定都**复用同一次调用**。
/// </para>
/// <para>
/// 只缓存**有消息标识**的条目：没有 EventId 的消息用时间戳当键会误命中。
/// </para>
/// </remarks>
public sealed class JudgeVerdictCache
{
    private sealed record Entry(bool Verdict, DateTimeOffset At);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;

    public JudgeVerdictCache(TimeSpan? ttl = null, int maxEntries = 5000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);

        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        _maxEntries = maxEntries;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>拼缓存键；没有消息标识返回 <c>null</c>（不缓存）。</summary>
    public static string? KeyOf(string sessionId, string? messageId) =>
        string.IsNullOrWhiteSpace(messageId) ? null : sessionId + "|" + messageId;

    public bool TryGet(string? key, out bool verdict)
    {
        verdict = false;
        if (key is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - entry.At > _ttl)
            {
                _entries.Remove(key);
                return false;
            }

            verdict = entry.Verdict;
            return true;
        }
    }

    public void Set(string? key, bool verdict)
    {
        if (key is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.Count >= _maxEntries)
            {
                PruneExpired();
            }

            _entries[key] = new Entry(verdict, DateTimeOffset.UtcNow);
        }
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _entries.Where(p => now - p.Value.At > _ttl).Select(p => p.Key).ToList())
        {
            _entries.Remove(key);
        }

        // 仍然满：丢最早的一半，别让缓存无限长（判定过期了也就多一次调用）
        if (_entries.Count >= _maxEntries)
        {
            foreach (var key in _entries.OrderBy(p => p.Value.At).Take(_entries.Count / 2).Select(p => p.Key).ToList())
            {
                _entries.Remove(key);
            }
        }
    }
}
