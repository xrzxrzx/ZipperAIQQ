using ZipperAIQQ.Agent;

namespace ZipperAIQQ.Server.Channels;

/// <summary><see cref="IChannelHealthLookup"/> 的 Server 实现（读共享注册表）。</summary>
public sealed class ChannelHealthLookup : IChannelHealthLookup
{
    private readonly ChannelHealthRegistry _registry;

    public ChannelHealthLookup(ChannelHealthRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    public bool IsChannelDown(string platform) =>
        _registry.Get(platform).IsChannelDown;
}
