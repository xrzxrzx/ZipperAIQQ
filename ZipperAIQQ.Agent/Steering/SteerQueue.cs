namespace ZipperAIQQ.Agent.Steering;

/// <summary>
/// 引导队列（R-04 / K-3）：运行中到达、要注入当前轮的消息文本。
/// </summary>
/// <remarks>
/// 只存文本：注入点是「下一次模型调用之前」，那时消息早已落库，
/// 这里再带一堆元数据没有意义，反而容易和批次合并口径打架。
/// </remarks>
public sealed class SteerQueue
{
    private readonly Queue<string> _items = new();
    private readonly object _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public void Enqueue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            _items.Enqueue(text.Trim());
        }
    }

    /// <summary>取走全部待注入文本（没有返回空）。</summary>
    public IReadOnlyList<string> Drain()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return [];
            }

            var all = _items.ToList();
            _items.Clear();
            return all;
        }
    }
}
