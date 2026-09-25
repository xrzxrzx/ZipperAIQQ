using Serilog;
using System.Collections.Concurrent;
using ZipperAIQQ.Core.Events.EventChannel;

namespace ZipperAIQQ.Core.Events;

public class EventBus : IEventBus
{
    ILogger _logger;
    bool _disposed = false;

    static readonly IDisposable EmptyIDisposable = new EmptyDisposable();
    sealed class EmptyDisposable : IDisposable { public void Dispose() { } }

    private ConcurrentDictionary<Type, EventChannelBase> _eventChannels = new ConcurrentDictionary<Type, EventChannelBase>();

    public EventBus(ILogger logger)
    {
        _logger = logger;
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : class
    {
        return Subscribe(null, handler);
    }

    public IDisposable Subscribe<T>(object? subscriber, Action<T> handler) where T : class
    {
        if (_disposed)
            return EmptyIDisposable;

        if(handler == null)
        {
            ArgumentNullException ex = new ArgumentNullException(nameof(handler));
            _logger.Error(ex, "订阅 handler 为空");
            throw ex;
        }

        if(_eventChannels.GetOrAdd(typeof(T), _ => new EventChannel<T>(_logger)) is not EventChannel<T> channel)
        {
            _logger.Error("创建事件通道失败，类型: {TypeName}", typeof(T).Name);
            throw new InvalidOperationException($"创建事件通道失败，类型: {typeof(T).Name}");
        }

        return channel.Add(subscriber, handler);
    }

    public bool Unsubscribe<T>(Action<T> handler) where T : class
    {
        if (_disposed)
            return false;

        if (handler == null)
        {
            _logger.Error(new ArgumentNullException(nameof(handler)), "退订 handler 为空");
            return false;
        }

        if(_eventChannels.TryGetValue(typeof(T), out var channel))
        {
            int removeCount = (channel as EventChannel<T>)?.Remove(handler) ?? 0;//别问这里加这么多问号合不合理，本身这里就不该出现null
            if(removeCount <= 0)
            {
                _logger.Warning("退订失败，没有订阅 {TypeName} 类型事件", typeof(T).Name);
                return false;
            }
            return true;
        }
        else
        {
            _logger.Warning("没有订阅这种事件类型 {TypeName}", typeof(T).Name);
            return false;
        }
    }

    public void UnsubscribeAll(object subscriber)
    {
        if (_disposed)
            return;

        if (subscriber == null)
        {
            _logger.Warning("尝试退订所有匿名订阅");
            return;
        }

        foreach (var channel in _eventChannels.Values)
        {
            channel.Remove(subscriber);
        }
    }

    public void Publish<T>(in T @event) where T : class
    {
        if (_disposed)
            return;

        if(_eventChannels.TryGetValue(typeof(T), out var channel))
        {
            (channel as EventChannel<T>)?.Publish(@event);
        }
    }

    public EventsState GetState()
    {
        return new EventsState(_eventChannels);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var channel in _eventChannels.Values)
        {
            channel.Dispose();
        }
        _eventChannels.Clear();
    }
}