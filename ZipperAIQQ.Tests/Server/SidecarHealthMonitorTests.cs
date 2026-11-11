using Serilog;
using ZipperAIQQ.Server.Alerts;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Tests.Server;

/// <summary>sidecar 看护：断开超阈值告警、节流、恢复后重置。</summary>
public sealed class SidecarHealthMonitorTests
{
    private sealed class FakeLogin : IChannelLogin
    {
        public bool IsConnected { get; set; }
        public string? SelfId => null;
        public string LoginState => IsConnected ? "Online" : "Offline";
        public string? LoginMessage => null;
        public string? LoginQrBase64 => null;
        public DateTimeOffset? LoginQrAt => null;
        public Task RequestLoginAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAlerts : IAlertSink
    {
        public List<AlertMessage> Sent { get; } = [];

        public Task<bool> RaiseAsync(AlertMessage alert, CancellationToken ct = default)
        {
            Sent.Add(alert);
            return Task.FromResult(true);
        }
    }

    private static (SidecarHealthMonitor Monitor, FakeLogin Login, FakeAlerts Alerts) Create()
    {
        var login = new FakeLogin();
        var alerts = new FakeAlerts();
        var monitor = new SidecarHealthMonitor(
            login, alerts, new LoggerConfiguration().CreateLogger(),
            alertAfter: TimeSpan.FromMinutes(3));

        return (monitor, login, alerts);
    }

    [Fact]
    public void Disconnect_below_threshold_does_not_alert()
    {
        var (monitor, login, alerts) = Create();
        var t0 = DateTimeOffset.UtcNow;

        login.IsConnected = false;
        monitor.CheckOnce(t0);
        monitor.CheckOnce(t0.AddMinutes(2));

        Assert.Empty(alerts.Sent);
    }

    [Fact]
    public void Disconnect_past_threshold_alerts_once_and_is_throttled()
    {
        var (monitor, login, alerts) = Create();
        var t0 = DateTimeOffset.UtcNow;

        login.IsConnected = false;
        monitor.CheckOnce(t0);
        monitor.CheckOnce(t0.AddMinutes(4));

        Assert.Single(alerts.Sent);

        monitor.CheckOnce(t0.AddMinutes(5));
        Assert.Single(alerts.Sent);
    }

    [Fact]
    public void Reconnect_resets_state()
    {
        var (monitor, login, alerts) = Create();
        var t0 = DateTimeOffset.UtcNow;

        login.IsConnected = false;
        monitor.CheckOnce(t0);
        monitor.CheckOnce(t0.AddMinutes(4));
        Assert.Single(alerts.Sent);

        login.IsConnected = true;
        monitor.CheckOnce(t0.AddMinutes(5));

        login.IsConnected = false;
        monitor.CheckOnce(t0.AddMinutes(20));
        monitor.CheckOnce(t0.AddMinutes(24));

        // 超过节流窗口（10 分钟）后允许再次告警
        Assert.Equal(2, alerts.Sent.Count);
    }
}
