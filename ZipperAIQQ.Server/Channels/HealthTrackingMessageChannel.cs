using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Failures;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 出站装饰器：把每一次发送的结果喂给 <see cref="ChannelHealthRegistry"/>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么放在最外层：只有最外层看到的是**最终结果**（节流之后、复合路由之后）。
/// 放在里面的话，节流抛的错、复合通道路由失败都统计不到。
/// </para>
/// <para>
/// 熔断打开后**快速失败**：不再真的去发，直接抛 <see cref="ChannelUnavailableException"/>。
/// 这样上层（Agent / 调度 / 邮件）拿到的仍是同一种语义异常，不用各自判断「现在还能不能发」。
/// </para>
/// </remarks>
public sealed class HealthTrackingMessageChannel : IMessageChannel
{
    private readonly IMessageChannel _inner;
    private readonly ChannelHealthRegistry _health;
    private readonly ILogger _logger;

    public HealthTrackingMessageChannel(
        IMessageChannel inner,
        ChannelHealthRegistry health,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _health = health;
        _logger = logger;
    }

    public string Platform => _inner.Platform;

    public string? SelfId => _inner.SelfId;

    public event Action<string>? SelfIdChanged
    {
        add => _inner.SelfIdChanged += value;
        remove => _inner.SelfIdChanged -= value;
    }

    public IReadOnlyList<string> Platforms => _inner.Platforms;

    public string? SelfIdFor(string platform) => _inner.SelfIdFor(platform);

    public ChannelCapabilities Capabilities => _inner.Capabilities;

    public Task StartAsync(CancellationToken ct = default) => _inner.StartAsync(ct);

    public IAsyncEnumerable<InboundEvent> ReceiveAsync(CancellationToken ct) => _inner.ReceiveAsync(ct);

    public async Task SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var monitor = _health.Get(message.Chat.Platform);

        if (monitor.IsChannelDown)
        {
            // 熔断中：别再真的去发（省一次网络往返，也让上层立刻拿到可判定的原因）
            throw new ChannelUnavailableException(
                message.Chat.Platform,
                $"{message.Chat.Platform} 通道已熔断（连续失败达阈值），正在等待恢复");
        }

        try
        {
            await _inner.SendAsync(message, ct).ConfigureAwait(false);
            monitor.RecordSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = SendFailureClassifier.Classify(ex);
            if (monitor.RecordFailure(kind, DateTimeOffset.UtcNow))
            {
                _logger.Warning(
                    "{Platform} 通道连续失败达阈值，已熔断（{Kind}）",
                    message.Chat.Platform, kind);
            }

            throw;
        }
    }
}
