using System.Text.Json;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Failures;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// <c>send_message</c> 要把失败原因报给上层 —— Agent 靠它决定要不要再问模型。
/// </summary>
public sealed class SendMessageToolTests
{
    private sealed class FailingChannel(Exception failure) : IMessageChannel
    {
        public string Platform => "qq";
        public ChannelCapabilities Capabilities => ChannelCapabilities.Text;
        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(OutgoingMessage message, CancellationToken ct) => throw failure;

        public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NoopMetrics : IMetricsStore
    {
        public Task RecordAsync(MetricEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MetricSummary>> GetSummaryAsync(
            int windowHours = 24, string? sessionId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetricSummary>>([]);

        public Task<IReadOnlyList<MetricSessionInfo>> ListSessionAsync(
            int windowHours = 24, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetricSessionInfo>>([]);
    }

    private sealed class NoopStore : IMessageStore
    {
        public Task<long> AppendAsync(IncomingMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> AppendOutgoingAsync(OutgoingMessage message, string senderUserId, DateTimeOffset sentAt, CancellationToken ct = default) =>
            Task.FromResult(0L);

        public Task<long> AppendCommandAsync(OutgoingMessage message, string senderUserId, DateTimeOffset sentAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMessage>> GetRecentAsync(string sessionId, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMessage>> GetRangeAsync(string sessionId, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMessage>> GetByIdsAsync(string sessionId, IReadOnlyList<long> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredMessage?> FindByMessageIdAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0L);

        public Task<IReadOnlyList<StoredMessage>> SearchAsync(string sessionId, string keyword, int limit, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMessage>> SearchAcrossSessionsAsync(string keyword, int limit, string? platform, string? sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> PurgeAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static async Task<(string Result, List<FailureKind> Kinds)> RunAsync(Exception failure)
    {
        var kinds = new List<FailureKind>();
        var tool = SendMessageTool.Create(
            new FailingChannel(failure),
            new NoopStore(),
            "bot",
            new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "10001" },
            maxMessages: 5,
            waitInterval: _ => Task.CompletedTask,
            faces: FaceCatalog.Empty,
            metrics: new NoopMetrics(),
            onSent: _ => { },
            onFailure: kind => kinds.Add(kind));

        var result = await tool.InvokeAsync(
            new AIFunctionArguments { ["messages"] = new[] { "测试" } },
            CancellationToken.None);

        return (result?.ToString() ?? string.Empty, kinds);
    }

    [Fact]
    public async Task Channel_unavailable_is_reported_to_caller()
    {
        var (result, kinds) = await RunAsync(new ChannelUnavailableException("qq", "ws aborted"));

        Assert.Equal([FailureKind.ChannelUnavailable], kinds);
        Assert.Contains("1 条失败", result);
    }

    [Fact]
    public async Task Unknown_failure_is_still_reported()
    {
        var (result, kinds) = await RunAsync(new InvalidOperationException("谁知道呢"));

        Assert.Equal([FailureKind.Unknown], kinds);
        Assert.Contains("1 条失败", result);
    }
}
