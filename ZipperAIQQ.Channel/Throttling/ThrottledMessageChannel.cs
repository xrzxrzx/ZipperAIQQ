using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Channel.Throttling;

/// <summary>
/// 给任意通道加「**全局**最短发送间隔 + 连发冷却」的装饰器。
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
/// **两层限速**：
/// ① <b>最小间隔</b>（默认 300ms）—— 平滑单条速率；
/// ② <b>连发冷却</b>（默认连发 10 条后歇 10 秒）—— 防止「一轮里连发十几条」这种最像机器人的形态。
/// 只做 ① 的话，持续按 300ms 发一分钟就是 200 条，照样危险。
/// </para>
/// <para>
/// 实现用「一把锁 + 上次发送时间」：持锁期间完成等待与发送，
/// 保证严格串行、严格间隔 —— 并发发送会让间隔失效。
/// </para>
/// </remarks>
public sealed class ThrottledMessageChannel : IMessageChannel, IContactDirectory
{
    private readonly IMessageChannel _inner;
    private readonly int _minIntervalMs;
    private readonly int _maxConsecutive;
    private readonly int _cooldownMs;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private int _consecutive;

    /// <summary>因触发冷却而实际等待过的次数（可观测用）。</summary>
    public int CooldownCount { get; private set; }


    public ThrottledMessageChannel(
        IMessageChannel inner,
        int minIntervalMs,
        int maxConsecutive = 10,
        int cooldownMs = 10_000)
    {
        ArgumentNullException.ThrowIfNull(inner);

        if (maxConsecutive <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConsecutive),
                "最大连发次数必须为正数 —— 0 或负数会让冷却逻辑永远不触发或立刻触发。");
        }

        _inner = inner;
        _minIntervalMs = Math.Max(0, minIntervalMs);
        _maxConsecutive = maxConsecutive;
        _cooldownMs = Math.Max(0, cooldownMs);
    }

    public string Platform => _inner.Platform;

    public ChannelCapabilities Capabilities => _inner.Capabilities;

    public Task StartAsync(CancellationToken ct = default) => _inner.StartAsync(ct);

    public IAsyncEnumerable<InboundEvent> ReceiveAsync(CancellationToken ct) => _inner.ReceiveAsync(ct);

    public async Task SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        if (_minIntervalMs <= 0 && _cooldownMs <= 0)
        {
            await _inner.SendAsync(message, ct).ConfigureAwait(false);
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;

            // ① 冷却期：连续发太多，先歇够时间再说
            if (now < _cooldownUntil)
            {
                var wait = _cooldownUntil - now;
                CooldownCount++;
                await Task.Delay(wait, ct).ConfigureAwait(false);
                _consecutive = 0;
                now = DateTimeOffset.UtcNow;
            }

            // ② 最小间隔。撞上间隔 = 这次属于「快速连发」，才计入连发次数。
            var elapsedMs = (now - _lastSentAt).TotalMilliseconds;
            var wasThrottled = _minIntervalMs > 0 && elapsedMs < _minIntervalMs;
            if (wasThrottled)
            {
                await Task.Delay((int)(_minIntervalMs - elapsedMs), ct).ConfigureAwait(false);
            }

            await _inner.SendAsync(message, ct).ConfigureAwait(false);
            _lastSentAt = DateTimeOffset.UtcNow;

            // ③ 连发计数
            if (wasThrottled)
            {
                _consecutive++;
                if (_consecutive >= _maxConsecutive && _cooldownMs > 0)
                {
                    _cooldownUntil = DateTimeOffset.UtcNow.AddMilliseconds(_cooldownMs);
                    _consecutive = 0;
                }
            }
            else
            {
                // 节奏正常（两次之间有自然间隔）→ 不算连发，计数清零
                _consecutive = 0;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── IContactDirectory：纯粹转发给内层（节流只管发消息，和查询无关）──
    // 这样上层从 IMessageChannel 拿到的对象同时也具备查询能力，不必额外注入一个依赖。

    private IContactDirectory Directory =>
        _inner as IContactDirectory
        ?? throw new InvalidOperationException($"内层通道 {_inner.GetType().Name} 不支持平台查询。");

    public Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default) =>
        Directory.GetFriendsAsync(ct);

    public Task<GroupInfo?> GetGroupAsync(string groupId, CancellationToken ct = default) =>
        Directory.GetGroupAsync(groupId, ct);

    public Task<IReadOnlyList<GroupMemberInfo>> GetGroupMembersAsync(
        string groupId, CancellationToken ct = default) =>
        Directory.GetGroupMembersAsync(groupId, ct);

    public Task<System.Text.Json.Nodes.JsonNode?> InvokeApiAsync(
        string action, System.Text.Json.Nodes.JsonObject? parameters, CancellationToken ct = default) =>
        Directory.InvokeApiAsync(action, parameters, ct);
}