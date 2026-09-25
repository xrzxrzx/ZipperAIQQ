using System;

namespace ZipperAIQQ.Core.Events.EventChannel
{
    internal abstract class EventChannelBase : IDisposable
    {
        public abstract int TotalSubscribers { get; }

        public abstract int Remove(object subscriber);

        public abstract void Dispose();
    }
}