using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Access;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Channels;
using ZipperAIQQ.Storage.Alerts;
using ZipperAIQQ.Storage.Owners;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Alerts;

/// <summary>
/// 把告警送到主人手里：**按平台降级**（QQ → 抖音 → 落库留档）。
/// </summary>
/// <remarks>
/// <para>
/// 这里只做「投递」，不判断该不该报警 —— 触发条件在各自的业务点（通道熔断、额度、
/// 定时失败），否则每个调用点都要复制一套阈值逻辑。
/// </para>
/// <para>
/// 抑制窗口按 (Kind, Platform) 去重，且只认**投递成功**的记录：
/// 全挂时留档但不算「已报过」，恢复后还能再报一次（D-04）。
/// </para>
/// </remarks>
public sealed class OwnerAlertService
{
    private readonly IAlertStore _alerts;
    private readonly IOwnerAccountStore _owners;
    private readonly IMessageChannel _channel;
    private readonly ChannelHealthRegistry _health;
    private readonly IRuntimeSettingsStore _settings;
    private readonly AlertOptions _options;
    private readonly ILogger _logger;
    private readonly EventBroadcaster? _events;

    private int _migrated;

    public OwnerAlertService(
        IAlertStore alerts,
        IOwnerAccountStore owners,
        IMessageChannel channel,
        ChannelHealthRegistry health,
        IRuntimeSettingsStore settings,
        AlertOptions options,
        ILogger logger,
        EventBroadcaster? events = null)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _alerts = alerts;
        _owners = owners;
        _channel = channel;
        _health = health;
        _settings = settings;
        _options = options;
        _logger = logger;
        _events = events;
    }

    /// <summary>
    /// 投递一条告警；返回是否**至少有一个平台发送成功**。
    /// </summary>
    public async Task<bool> RaiseAsync(AlertMessage alert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        if (await IsSuppressedAsync(alert, ct).ConfigureAwait(false))
        {
            _logger.Debug("告警被抑制（{Kind}/{Platform}）", alert.Kind, alert.Platform);
            return false;
        }

        var targets = await LoadTargetsAsync(ct).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            await RecordAsync(alert, null, false, "没有配置跨平台主人账号", ct).ConfigureAwait(false);
            _logger.Warning("告警无处可发（没配主人）：{Kind}", alert.Kind);
            return false;
        }

        // 先发「出事平台」的主人，再发其他平台 —— 断网时 QQ 侧主人可能恰好收不到
        var ordered = targets
            .Where(t => string.Equals(t.Platform, alert.Platform, StringComparison.OrdinalIgnoreCase))
            .Concat(targets.Where(t => !string.Equals(t.Platform, alert.Platform, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var anyOk = false;
        foreach (var target in ordered)
        {
            var chat = new ChatRef { Platform = target.Platform, Type = ChatType.Private, Id = target.UserId };

            if (_health.Get(target.Platform).IsChannelDown)
            {
                await RecordAsync(alert, target, false, "通道熔断，跳过", ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await _channel.SendAsync(
                    new OutgoingMessage { Chat = chat, Segments = [new TextSegment(alert.Text)] },
                    ct).ConfigureAwait(false);

                anyOk = true;
                await RecordAsync(alert, target, true, null, ct).ConfigureAwait(false);

                // 降级链：成功就停，不再往下一个平台发（否则主人会收到两遍）
                break;
            }
            catch (Exception ex)
            {
                await RecordAsync(alert, target, false, ex.Message, ct).ConfigureAwait(false);
                _logger.Warning(ex, "告警投递失败：{Kind} → {Platform}", alert.Kind, target.Platform);
            }
        }

        if (!anyOk)
        {
            _logger.Warning("告警所有通道都失败，已留档待补发：{Kind}", alert.Kind);
        }

        // WS 广播（D-6）：客户端连 /events 时能实时看到；没客户端时 Publish 自己会跳过
        _events?.Publish("alert", new
        {
            kind = alert.Kind.ToString(),
            platform = alert.Platform,
            ok = anyOk,
            text = alert.Text,
            at = alert.At,
        });

        return anyOk;
    }

    private async Task<bool> IsSuppressedAsync(AlertMessage alert, CancellationToken ct)
    {
        if (_options.SuppressMinutes <= 0)
        {
            return false;
        }

        var since = alert.At.AddMinutes(-_options.SuppressMinutes);
        return await _alerts
            .ExistsRecentAsync(alert.Kind.ToString(), alert.Platform, since, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 主人账号：首次使用时从 <c>access.{platform}.owner</c> 迁一份到镜像表。
    /// </summary>
    /// <remarks>
    /// 单向同步（settings → 表）：权限判定的唯一真源仍是 settings，
    /// 这里只解决「一次问出主人在哪些平台」，不引入第二套真相。
    /// </remarks>
    private async Task<IReadOnlyList<OwnerAccount>> LoadTargetsAsync(CancellationToken ct)
    {
        var existing = await _owners.ListAsync(ct).ConfigureAwait(false);
        if (existing.Count > 0 || Interlocked.Exchange(ref _migrated, 1) == 1)
        {
            return existing;
        }

        var platforms = _channel.Platforms;
        var discovered = new List<OwnerAccount>();

        foreach (var platform in platforms)
        {
            var owner = await AccessSettings.GetOwnerAsync(_settings, platform, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(owner))
            {
                discovered.Add(new OwnerAccount(platform, owner));
            }
        }

        if (discovered.Count == 0)
        {
            return [];
        }

        var added = await _owners.UpsertAsync(discovered, ct).ConfigureAwait(false);
        _logger.Information("主人账号镜像已迁移：{Count} 条（新增 {Added}）", discovered.Count, added);

        return await _owners.ListAsync(ct).ConfigureAwait(false);
    }

    private Task RecordAsync(
        AlertMessage alert,
        OwnerAccount? target,
        bool ok,
        string? error,
        CancellationToken ct) =>
        _alerts.AppendAsync(
            new AlertRecord(
                Id: 0,
                Kind: alert.Kind.ToString(),
                Platform: target?.Platform ?? alert.Platform,
                Target: target?.UserId,
                Ok: ok,
                Error: error,
                CreatedAt: alert.At),
            ct);
}
