using System.Collections.Concurrent;
using ZipperAIQQ.Core.Events.EventChannel;

namespace ZipperAIQQ.Core.Events;

public class EventsState
{
    public int TotalEventTypes { get; }
    public int TotalSubscribers { get; }
    public List<EventState> EventStates { get; }

    internal EventsState(ConcurrentDictionary<Type, EventChannelBase> eventChannels)
    {
        EventStates = new List<EventState>();

        TotalEventTypes = eventChannels.Count;
        int totalSubscribers = 0;

        foreach (var channelKeyValue in eventChannels)
        {
            EventStates.Add(new EventState(channelKeyValue.Key, channelKeyValue.Value.TotalSubscribers));
            totalSubscribers += channelKeyValue.Value.TotalSubscribers;
        }

        TotalSubscribers = totalSubscribers;
    }
}