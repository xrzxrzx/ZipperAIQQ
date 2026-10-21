using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;
using ZipperAIQQ.Agent.Events;
using ZipperAIQQ.Core.Events;
using ZipperAIQQ.Server.Dto;

namespace ZipperAIQQ.Server.Api;

/// <summary>
/// WS 事件广播（架构 §3.5）：每个客户端一条无界队列，帧格式固定
/// <c>{ type, seq, time, data }</c>（<c>time</c> 为 Unix 毫秒）。
/// </summary>
/// <remarks>
/// <para>
/// 队列用 <see cref="Channel{T}"/> 而不是"直接遍历 socket 发送"：
/// 一个慢客户端不能拖死广播方（<c>TryWrite</c> 立即返回）。
/// </para>
/// <para>
/// 现在只转发三类：<c>hello</c> / <c>qq.state</c> / <c>agent.event</c>。
/// 消息收发事件等到 M2-D 做"聊天列表实时刷新"时再加。
/// </para>
/// </remarks>
public sealed class EventBroadcaster : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();
    private readonly ILogger _logger;
    private readonly IDisposable _driftSubscription;
    private readonly IDisposable _degradedSubscription;
    private long _seq;

    public EventBroadcaster(IEventBus bus, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        _driftSubscription = bus.Subscribe<DriftDetectedEvent>(e =>
            Publish("agent.event", new { kind = "drift", sessionId = e.SessionId, detail = $"{e.Kind}/{e.Detail}" }));

        _degradedSubscription = bus.Subscribe<SendDegradedEvent>(e =>
            Publish("agent.event", new { kind = "degraded", sessionId = e.SessionId, detail = $"sent={e.SentCount}" }));
    }

    /// <summary>当前连着多少个客户端。</summary>
    public int ClientCount => _clients.Count;

    /// <summary>为一个新连接建立队列。</summary>
    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var id = Guid.NewGuid();

        // 完全限定：本项目的 ZipperAIQQ.Channel 命名空间会抢走 Channel 这个名字
        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        _clients[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_clients.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>广播一帧；没有任何客户端时什么都不做。</summary>
    public void Publish(string type, object? data)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        var frame = JsonSerializer.Serialize(
            new
            {
                type,
                seq = Interlocked.Increment(ref _seq),
                time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                data,
            },
            ApiJson.Options);

        foreach (var channel in _clients.Values)
        {
            channel.Writer.TryWrite(frame);
        }
    }

    /// <summary>握手帧内容（客户端据此判断版本与能力）。</summary>
    public static object HelloData() => new
    {
        serverVersion = Handlers.PingAction.ServerVersion,
        capabilities = new[] { "qq", "agent", "session" },
    };

    public void Dispose()
    {
        _driftSubscription.Dispose();
        _degradedSubscription.Dispose();

        foreach (var channel in _clients.Values)
        {
            channel.Writer.TryComplete();
        }

        _clients.Clear();
        _logger.Debug("事件广播已停止");
    }
}
