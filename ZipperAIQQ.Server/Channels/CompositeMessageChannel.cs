using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 复合通道：把多个平台通道**合并成一条**（入站合并、出站按平台路由）。
/// </summary>
/// <remarks>
/// <para>
/// 存在理由（决策三）：上层（<c>MessageRouter</c> / Agent / 工具）只想"收一条流、发一条流"，
/// 不想每加一个平台就多一个循环、多一处分支。所以「多平台」这件事**在通道层收口**。
/// </para>
/// <para>
/// <b>只包 <see cref="IMessageChannel"/></b>：邮件这类「只收不发」的源走
/// <see cref="IMessageSource"/>，由各自的管道消费（邮件接入设计 §2.1），不混进来 ——
/// 混进来就会逼出一个永远抛异常的 <c>SendAsync</c>。
/// </para>
/// <para>
/// <b>故障隔离</b>：任一子通道启动失败或接收流中断，只记日志、不拖垮其他通道
/// （验收：杀 sidecar 不影响 QQ / 邮件）。
/// </para>
/// <para>
/// ⚠️ <see cref="Platform"/> 仍返回**第一个子通道**的平台，只为兼容既有的单值消费者
/// （如主人反馈拼 <c>ChatRef</c>）。多平台场景一律用 <see cref="Platforms"/> /
/// <see cref="SelfIdFor"/> / 按 <see cref="ChatRef.Platform"/> 路由，别读 <see cref="Platform"/>。
/// </para>
/// <para>
/// 同时实现 <see cref="IContactDirectory"/>（转发给**主平台**）：<c>ChatAgent</c> 靠
/// <c>_channel is IContactDirectory</c> 决定要不要给模型联系人工具（ChatAgent.cs:573），
/// 不实现的话那些工具会在调用时才炸。多平台下的按平台查询属于 S3-C（决策四）。
/// </para>
/// </remarks>
public sealed class CompositeMessageChannel : IMessageChannel, IContactDirectory
{
    private readonly IReadOnlyList<IMessageChannel> _channels;
    private readonly Dictionary<string, IMessageChannel> _byPlatform;

    /// <summary>合并后的入站流；单读者（<c>MessageRouter</c> 一个循环）。</summary>
    /// <remarks>
    /// ⚠️ 必须写全 <c>System.Threading.Channels.Channel</c>：本文件命名空间是 <c>ZipperAIQQ.Server.Channels</c>，
    /// 而项目里有个 <c>ZipperAIQQ.Channel</c> 命名空间，裸写会被解析到它（CS0234）。
    /// </remarks>
    private readonly Channel<InboundEvent> _merged = System.Threading.Channels.Channel.CreateUnbounded<InboundEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly ILogger _logger;
    private int _receiveStarted;

    public CompositeMessageChannel(IEnumerable<IMessageChannel> channels, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(logger);

        _channels = channels.ToList();
        _logger = logger;

        if (_channels.Count == 0)
        {
            // 一个通道都没有等于"收不到也发不出"，默默跑起来只会让人以为服务正常
            throw new ArgumentException("复合通道至少要有一个子通道。", nameof(channels));
        }

        _byPlatform = new Dictionary<string, IMessageChannel>(StringComparer.Ordinal);
        foreach (var channel in _channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Platform))
            {
                throw new ArgumentException(
                    $"子通道 {channel.GetType().Name} 的 Platform 为空。", nameof(channels));
            }

            // 同平台两个实现 ⇒ 出站路由变成掷骰子（同一个 sessionId 前缀对应两条连接）
            if (!_byPlatform.TryAdd(channel.Platform, channel))
            {
                throw new ArgumentException(
                    $"平台 {channel.Platform} 被注册了两次，出站将无法确定走哪条通道。", nameof(channels));
            }
        }
    }

    /// <summary>主平台 = 第一个子通道（兼容旧单值消费者，见类型注释）。</summary>
    public string Platform => _channels[0].Platform;

    /// <summary>已注册的全部平台（顺序与注册顺序一致）。</summary>
    public IReadOnlyList<string> Platforms => [.. _byPlatform.Keys];

    /// <summary>主平台的自身账号（兼容旧单值消费者）。</summary>
    public string? SelfId => _channels[0].SelfId;

    /// <inheritdoc />
    public string? SelfIdFor(string platform) =>
        _byPlatform.TryGetValue(platform, out var channel) ? channel.SelfId : null;

    /// <inheritdoc />
    /// <remarks>所有子通道的变更都往外抛（不吞：吞了就没法持久化）。</remarks>
    public event Action<string>? SelfIdChanged
    {
        add
        {
            foreach (var channel in _channels)
            {
                channel.SelfIdChanged += value;
            }
        }

        remove
        {
            foreach (var channel in _channels)
            {
                channel.SelfIdChanged -= value;
            }
        }
    }

    /// <summary>能力 = 各子通道能力的并集（"这条复合通道能做什么"）。</summary>
    public ChannelCapabilities Capabilities
    {
        get
        {
            var union = ChannelCapabilities.None;
            foreach (var channel in _channels)
            {
                union |= channel.Capabilities;
            }

            return union;
        }
    }

    /// <summary>并行启动所有子通道；单个失败只记日志（不拖垮其他平台）。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var failures = 0;

        foreach (var channel in _channels)
        {
            try
            {
                await channel.StartAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // QQ 连不上不该让抖音也没了；反之亦然
                failures++;
                _logger.Error(ex, "通道 {Platform} 启动失败（其余通道继续）", channel.Platform);
            }
        }

        if (failures == _channels.Count)
        {
            _logger.Error("全部 {Count} 个通道都启动失败：入站将收不到任何事件", failures);
        }
    }

    /// <summary>合并所有子通道的入站事件。</summary>
    /// <remarks>
    /// 首个读者到达时才拉起各通道的泵（用调用方的 <paramref name="ct"/> 当生命周期）——
    /// 这样不需要额外维护一套启停状态，宿主停止时泵自然结束。
    /// </remarks>
    public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _receiveStarted, 1) == 0)
        {
            foreach (var channel in _channels)
            {
                _ = PumpAsync(channel, ct);
            }
        }

        await foreach (var evt in _merged.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <summary>按 <see cref="ChatRef.Platform"/> 路由出站；未知平台**明确报错**，不静默丢。</summary>
    public Task SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!_byPlatform.TryGetValue(message.Chat.Platform, out var target))
        {
            // 静默丢弃 = 模型以为发出去了（FR-2.3 的反面教材）
            throw new InvalidOperationException(
                $"没有平台 {message.Chat.Platform} 的通道，无法发送到 {message.Chat.SessionId}。" +
                $"已注册平台：{string.Join(" / ", _byPlatform.Keys)}");
        }

        return target.SendAsync(message, ct);
    }

    /// <summary>把一条子通道的事件搬进合并流；该通道断了不影响其他通道。</summary>
    private async Task PumpAsync(IMessageChannel channel, CancellationToken ct)
    {
        try
        {
            await foreach (var evt in channel.ReceiveAsync(ct).ConfigureAwait(false))
            {
                await _merged.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }

            _logger.Warning("通道 {Platform} 的入站流已结束", channel.Platform);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            // 一个平台断了，其他平台必须继续（验收：杀 sidecar 不影响 QQ / 邮件）
            _logger.Error(ex, "通道 {Platform} 的入站流异常结束（其余通道继续）", channel.Platform);
        }
    }

    // ─────────────────────────── IContactDirectory ───────────────────────────
    // 一律转发给**主平台**通道（今天就是 QQ）：多平台按平台查询属 S3-C（决策四）。

    private IContactDirectory PrimaryDirectory =>
        _channels[0] as IContactDirectory
        ?? throw new InvalidOperationException($"主通道 {_channels[0].GetType().Name} 不支持平台联系人查询。");

    /// <inheritdoc />
    public Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default) =>
        PrimaryDirectory.GetFriendsAsync(ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken ct = default) =>
        PrimaryDirectory.GetGroupsAsync(ct);

    /// <inheritdoc />
    public Task SetSignatureAsync(string text, CancellationToken ct = default) =>
        PrimaryDirectory.SetSignatureAsync(text, ct);

    /// <inheritdoc />
    public Task SendPokeAsync(string userId, string? groupId, CancellationToken ct = default) =>
        PrimaryDirectory.SendPokeAsync(userId, groupId, ct);

    /// <inheritdoc />
    public Task<GroupInfo?> GetGroupAsync(string groupId, CancellationToken ct = default) =>
        PrimaryDirectory.GetGroupAsync(groupId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<GroupMemberInfo>> GetGroupMembersAsync(
        string groupId, CancellationToken ct = default) =>
        PrimaryDirectory.GetGroupMembersAsync(groupId, ct);

    /// <inheritdoc />
    public Task<System.Text.Json.Nodes.JsonNode?> InvokeApiAsync(
        string action, System.Text.Json.Nodes.JsonObject? parameters, CancellationToken ct = default) =>
        PrimaryDirectory.InvokeApiAsync(action, parameters, ct);
}
