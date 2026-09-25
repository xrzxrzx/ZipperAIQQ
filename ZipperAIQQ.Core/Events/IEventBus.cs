namespace ZipperAIQQ.Core.Events;

public interface IEventBus : IDisposable
{
    IDisposable Subscribe<T>(Action<T> action) where T : class;
    IDisposable Subscribe<T>(object? subscriber, Action<T> action) where T : class;

    bool Unsubscribe<T>(Action<T> action) where T : class;
    void UnsubscribeAll(object subscriber);

    void Publish<T>(in T @event) where T : class;

    EventsState GetState();
}