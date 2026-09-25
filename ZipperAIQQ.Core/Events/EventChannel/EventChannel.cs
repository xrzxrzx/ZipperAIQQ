using Serilog;

namespace ZipperAIQQ.Core.Events.EventChannel;

internal class EventChannel<T> : EventChannelBase where T : class
{
    ILogger _logger;
    Subscription<T>[] _subscriptions;

    public override int TotalSubscribers => _subscriptions.Length;

    public EventChannel(ILogger logger)
    {
        _logger = logger;
        _subscriptions = new Subscription<T>[0];
    }

    public IDisposable Add(object? subscriber, Action<T> handler)
    {
        var subscriptions = Volatile.Read(ref _subscriptions);
        var subscription = new Subscription<T>(handler, subscriber, this);

        Subscription<T>[] tempSubscriptions = new Subscription<T>[subscriptions.Length + 1];
        Array.Copy(subscriptions, tempSubscriptions, subscriptions.Length);

        tempSubscriptions[tempSubscriptions.Length - 1] = subscription;

        Volatile.Write(ref _subscriptions, tempSubscriptions);

        return subscription;
    }

    public int Remove(Action<T> handler)
    {
        int length = _subscriptions.Length;
        _subscriptions = Array.FindAll(_subscriptions, s => s.Handler != handler);
        return length - _subscriptions.Length;
    }

    public int Remove(Subscription<T> subscription)
    {
        int length = _subscriptions.Length;
        _subscriptions = Array.FindAll(_subscriptions, s => s != subscription);
        return length - _subscriptions.Length;
    }

    public override int Remove(object subscriber)
    {
        int length = _subscriptions.Length;
        _subscriptions = Array.FindAll(_subscriptions, s => s.Subscriber != subscriber);
        return length - _subscriptions.Length;
    }

    public void Publish(in T @event)
    {
        var subscriptions = Volatile.Read(ref _subscriptions);
        foreach (var subscription in subscriptions)
        {
            if (subscription.IsDisposed)
            {
                continue;
            }

            try
            {
                subscription.Handler(@event);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "执行事件处理程序时发生异常");
            }
        }
    }

    public override void Dispose()
    {
        var subscriptions = Volatile.Read(ref _subscriptions);
        foreach (var subscription in subscriptions)
        {
            subscription.MarkAsDisposed();
        }
        _subscriptions = Array.Empty<Subscription<T>>();
    }
}