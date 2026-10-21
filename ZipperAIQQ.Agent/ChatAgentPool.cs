using System.Collections.Concurrent;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Agent;

/// <summary>agent 实例池（M1-B 简版：只增不卸，M1-C 再加 LRU 冷热）。</summary>
public sealed class ChatAgentPool : IChatAgentPool
{
    private readonly ConcurrentDictionary<string, IChatAgent> _agents = new(StringComparer.Ordinal);
    private readonly Func<ChatRef, IChatAgent> _factory;

    public ChatAgentPool(Func<ChatRef, IChatAgent> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public int LoadedCount => _agents.Count;

    public IChatAgent GetOrCreate(ChatRef chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        return _agents.GetOrAdd(chat.SessionId, _ => _factory(chat));
    }
}