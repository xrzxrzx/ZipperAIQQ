namespace ZipperAIQQ.Core.Events;

public class EventState
{
    public Type EventType { get; }
    public int SubscriberCount { get; }

    internal EventState(Type eventType, int subscriberCount)
    {
        EventType = eventType;
        SubscriberCount = subscriberCount;
    }
}