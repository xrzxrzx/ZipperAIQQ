using System.Collections.Concurrent;
using ZipperAIQQ.Channel.Reliability;

namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 各通道健康状态（平台 → 监视器），进程内唯一。
/// </summary>
/// <remarks>
/// 必须是**共享实例**：恢复循环往里写、Agent 短路判据和 <c>/status</c> 从里读。
/// 各建一份的话，会出现「循环认为已恢复、Agent 认为还断着」这种自相矛盾的状态。
/// </remarks>
public sealed class ChannelHealthRegistry
{
    private readonly ConcurrentDictionary<string, ChannelHealthMonitor> _monitors =
        new(StringComparer.OrdinalIgnoreCase);

    public ChannelHealthMonitor Get(string platform) =>
        _monitors.GetOrAdd(platform ?? throw new ArgumentNullException(nameof(platform)),
            p => new ChannelHealthMonitor(p));

    public IReadOnlyDictionary<string, ChannelHealthMonitor> All => _monitors;
}
