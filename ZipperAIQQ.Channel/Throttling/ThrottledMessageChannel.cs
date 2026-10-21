using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Channel.Throttling;

/// <summary>
/// 给任意通道加「**全局**最短发送间隔」的装饰器。
/// </summary>
/// <remarks>
/// <para>
/// **为什么是全局而不是每会话**：风控看的是**同一个 QQ 号**的发信速率，
/// 跟你把它发到了哪个群、哪个私聊无关。每会话各算各的，等于没限速。
/// </para>
/// <para>
/// **为什么在通道层**：所有出站消息都必须过这里，不存在"某条路径绕过限流"。
/// 上层（Agent/工具）不需要知道限速的存在。
/// </para>
/// <para>
/// 实现用「一把锁 + 上次发送时间」：持锁期间完成等待与发送，
/// 保证严格串行、严格间隔 —— 并发发送会让间隔失效。
/// </para>
/// </remarks>
public sealed class ThrottledMessageChannel : IMessageChannel
{
    private readonly IMessageChannel _inner;
    private readonly int _minIntervalMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;

    public ThrottledMessageChannel(IMessageChannel inner, int minIntervalMs)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _minIntervalMs = Math.Max(0, minIntervalMs);
    }

    public string Platform => _inner.Platform;

    public ChannelCapabilities Capabilities => _inner.Capabilities;

    public Task StartAsync(CancellationToken ct = default) => _inner.StartAsync(ct);

    public IAsyncEnumerable<InboundEvent> ReceiveAsync(CancellationToken ct) => _inner.ReceiveAsync(ct);

    public async Task SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        if (_minIntervalMs <= 0)
        {
            await _inner.SendAsync(message, ct).ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = (DateTimeOffset.UtcNow - _lastSentAt).TotalMilliseconds;
            var waitMs = _minIntervalMs - elapsed;
            if (waitMs > 0)
            {
                await Task.Delay((int)waitMs, ct).ConfigureAwait(false);
            }

            await _inner.SendAsync(message, ct).ConfigureAwait(false);
            _lastSentAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}