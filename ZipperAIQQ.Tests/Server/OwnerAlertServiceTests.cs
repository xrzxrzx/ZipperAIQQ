using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Channel.Reliability;
using ZipperAIQQ.Server.Alerts;
using ZipperAIQQ.Server.Channels;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage.Alerts;
using ZipperAIQQ.Storage.Owners;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 告警投递：按平台降级、抑制窗口、熔断平台跳过（D-04）。
/// </summary>
public sealed class OwnerAlertServiceTests
{
    private sealed class FakeAlertStore : IAlertStore
    {
        public List<AlertRecord> Rows { get; } = [];

        public bool RecentExists { get; set; }

        public Task AppendAsync(AlertRecord record, CancellationToken ct = default)
        {
            Rows.Add(record);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsRecentAsync(string kind, string? platform, DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult(RecentExists);

        public Task<IReadOnlyList<AlertRecord>> ListRecentAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AlertRecord>>(Rows);
    }

    private sealed class FakeOwners(params OwnerAccount[] accounts) : IOwnerAccountStore
    {
        public Task<IReadOnlyList<OwnerAccount>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OwnerAccount>>(accounts);

        public Task<int> UpsertAsync(IEnumerable<OwnerAccount> toAdd, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeChannel : IMessageChannel
    {
        public List<ChatRef> Sent { get; } = [];

        public string? FailOnPlatform { get; set; }

        public string Platform => "qq";

        public IReadOnlyList<string> Platforms => ["qq", "douyin"];

        public ChannelCapabilities Capabilities => ChannelCapabilities.Text;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(OutgoingMessage message, CancellationToken ct)
        {
            if (string.Equals(message.Chat.Platform, FailOnPlatform, StringComparison.Ordinal))
            {
                return Task.FromException(new InvalidOperationException("send failed"));
            }

            Sent.Add(message.Chat);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeSettings : IRuntimeSettingsStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.FromHours(8));

    private static (OwnerAlertService Service, FakeChannel Channel, FakeAlertStore Alerts, ChannelHealthRegistry Health)
        Create(IOwnerAccountStore owners, bool recentExists = false)
    {
        var alerts = new FakeAlertStore { RecentExists = recentExists };
        var channel = new FakeChannel();
        var health = new ChannelHealthRegistry(new ChannelReliabilityOptions());
        var service = new OwnerAlertService(
            alerts,
            owners,
            channel,
            health,
            new FakeSettings(),
            new AlertOptions { SuppressMinutes = 10 },
            new LoggerConfiguration().CreateLogger());

        return (service, channel, alerts, health);
    }

    private static AlertMessage Alert(string? platform = "qq") =>
        new(AlertKind.ChannelDown, platform, "通道挂了", Now);

    [Fact]
    public async Task Sends_to_owner_and_records_success()
    {
        var (service, channel, alerts, _) = Create(new FakeOwners(
            new OwnerAccount("qq", "10001"),
            new OwnerAccount("douyin", "20002")));

        var ok = await service.RaiseAsync(Alert());

        Assert.True(ok);
        Assert.Single(channel.Sent);
        Assert.Equal("qq", channel.Sent[0].Platform);
        Assert.Contains(alerts.Rows, r => r.Ok);
    }

    [Fact]
    public async Task Suppression_window_prevents_resend()
    {
        var (service, channel, _, _) = Create(new FakeOwners(new OwnerAccount("qq", "10001")), recentExists: true);

        var ok = await service.RaiseAsync(Alert());

        Assert.False(ok);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task Melted_platform_is_skipped()
    {
        var (service, channel, alerts, health) = Create(new FakeOwners(
            new OwnerAccount("qq", "10001"),
            new OwnerAccount("douyin", "20002")));

        var douyin = health.Get("douyin");
        for (var i = 0; i < 3; i++)
        {
            douyin.RecordFailure(ZipperAIQQ.Channel.Failures.FailureKind.ChannelUnavailable, Now);
        }

        // 告警平台是抖音（已熔断）→ 第一跳被跳过，降级到 QQ 成功
        var ok = await service.RaiseAsync(Alert("douyin"));

        Assert.True(ok);
        Assert.Single(channel.Sent);
        Assert.Equal("qq", channel.Sent[0].Platform);
        Assert.Contains(alerts.Rows, r => !r.Ok && r.Error == "通道熔断，跳过");
    }

    [Fact]
    public async Task All_targets_failing_is_recorded_for_resend()
    {
        var (service, channel, alerts, _) = Create(new FakeOwners(new OwnerAccount("qq", "10001")));
        channel.FailOnPlatform = "qq";

        var ok = await service.RaiseAsync(Alert());

        Assert.False(ok);
        Assert.Contains(alerts.Rows, r => !r.Ok && r.Error is not null);
    }

    [Fact]
    public async Task No_owner_records_a_reason()
    {
        var (service, _, alerts, _) = Create(new FakeOwners());

        var ok = await service.RaiseAsync(Alert());

        Assert.False(ok);
        Assert.Contains(alerts.Rows, r => !r.Ok && r.Error == "没有配置跨平台主人账号");
    }
}
