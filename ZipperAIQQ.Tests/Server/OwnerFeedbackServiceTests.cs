using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Failures;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Server.Alerts;
using ZipperAIQQ.Server.Feedback;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 主人反馈发送失败要走告警降级链（D-5），不能只打一行 warn。
/// </summary>
public sealed class OwnerFeedbackServiceTests
{
    private sealed class FakeSettings : IRuntimeSettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal)
        {
            ["feedback.qq.enabled"] = "true",
            ["feedback.qq.types"] = "MessageRecalled",
            ["access.qq.owner"] = "10001",
        };

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string? value, CancellationToken ct = default)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailingChannel : IMessageChannel
    {
        public string Platform => "qq";
        public ChannelCapabilities Capabilities => ChannelCapabilities.Text;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(OutgoingMessage message, CancellationToken ct) =>
            Task.FromException(new ChannelUnavailableException("qq", "down"));
        public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeStore : IMessageStore
    {
        public Task<long> AppendAsync(IncomingMessage message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> AppendOutgoingAsync(OutgoingMessage message, string senderUserId, DateTimeOffset sentAt, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> AppendCommandAsync(OutgoingMessage message, string senderUserId, DateTimeOffset sentAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredMessage>> GetRecentAsync(string sessionId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredMessage>> GetRangeAsync(string sessionId, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredMessage>> GetByIdsAsync(string sessionId, IReadOnlyList<long> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StoredMessage?> FindByMessageIdAsync(string sessionId, string messageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<StoredMessage>> SearchAsync(string sessionId, string keyword, int limit, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredMessage>> SearchAcrossSessionsAsync(string keyword, int limit, string? platform, string? sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeSink : IAlertSink
    {
        public List<AlertMessage> Raised { get; } = [];

        public Task<bool> RaiseAsync(AlertMessage alert, CancellationToken ct = default)
        {
            Raised.Add(alert);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task Failed_feedback_raises_an_alert()
    {
        var sink = new FakeSink();
        var service = new OwnerFeedbackService(
            new FakeSettings(),
            new FakeStore(),
            new FailingChannel(),
            new LoggerConfiguration().CreateLogger(),
            sink);

        await service.HandleAsync(new IncomingNotice
        {
            Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "20002" },
            Type = NoticeType.MessageRecalled,
            RawType = "recall",
            Actor = new SenderInfo { UserId = "20002" },
            OccurredAt = BeijingTime.Now,
        });

        var alert = Assert.Single(sink.Raised);
        Assert.Equal(AlertKind.OwnerFeedbackFailed, alert.Kind);
        Assert.Contains("主人反馈发送失败", alert.Text);
    }
}
