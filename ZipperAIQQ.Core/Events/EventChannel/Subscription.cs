namespace ZipperAIQQ.Core.Events.EventChannel;

internal class Subscription<T> : IDisposable where T : class
{
    public Action<T> Handler { get; }
    public object? Subscriber { get; }
    public bool IsDisposed { get => isDisposed; private set => isDisposed = value; }

    private EventChannel<T> _eventChannel;
    private volatile bool isDisposed;

    public Subscription(Action<T> handler, object? subscriber, EventChannel<T> eventChannel)
    {
        Handler = handler;
        Subscriber = subscriber;
        IsDisposed = false;
        _eventChannel = eventChannel;
    }

    public void MarkAsDisposed()
    {
        IsDisposed = true;
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;

        MarkAsDisposed();
        _eventChannel.Remove(this);
    }
}