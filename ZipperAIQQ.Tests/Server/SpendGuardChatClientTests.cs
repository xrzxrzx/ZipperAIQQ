using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Server.Alerts;
using ZipperAIQQ.Server.Billing;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 额度闸门：关键放行、非关键拦截、没数据不拦（D-03 / E-4）。
/// </summary>
public sealed class SpendGuardChatClientTests
{
    private sealed class StubClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedSpend(SpendSnapshot snapshot) : ISpendReader
    {
        public Task<SpendSnapshot> ReadAsync(CancellationToken ct = default) => Task.FromResult(snapshot);
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

    private static (SpendGuardChatClient Guard, StubClient Inner, FakeSink Sink) Create(
        SpendSnapshot snapshot,
        QuotaOptions options)
    {
        var inner = new StubClient();
        var sink = new FakeSink();
        var guard = new SpendGuardChatClient(
            inner,
            new FixedSpend(snapshot),
            options,
            new LoggerConfiguration().CreateLogger(),
            sink);

        return (guard, inner, sink);
    }

    private static readonly QuotaOptions Enabled = new()
    {
        Enabled = true,
        LimitCny = 5m,
        WarnRatio = 0.7m,
    };

    private static SpendSnapshot OverLimit => new(6m, "balance", 0m, HasData: true);

    [Fact]
    public async Task Disabled_quota_never_blocks()
    {
        var (guard, inner, _) = Create(OverLimit, new QuotaOptions { Enabled = false });

        await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Critical_call_passes_at_hard_limit()
    {
        var (guard, inner, sink) = Create(OverLimit, Enabled);

        using (CallPurposeScope.Begin(CallPurpose.UserReply))
        {
            await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }

        Assert.Equal(1, inner.Calls);
        Assert.Empty(sink.Raised);
    }

    [Fact]
    public async Task Non_critical_call_is_blocked_and_alerted()
    {
        var (guard, inner, sink) = Create(OverLimit, Enabled);

        using (CallPurposeScope.Begin(CallPurpose.MailDigest))
        {
            await Assert.ThrowsAsync<QuotaExceededException>(
                () => guard.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        }

        Assert.Equal(0, inner.Calls);
        Assert.Contains(sink.Raised, a => a.Kind == AlertKind.QuotaHardStop);
    }

    [Fact]
    public async Task Missing_data_does_not_block()
    {
        var (guard, inner, _) = Create(new SpendSnapshot(0m, "none", null, HasData: false), Enabled);

        using (CallPurposeScope.Begin(CallPurpose.Compression))
        {
            await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Warn_threshold_alerts_but_still_passes()
    {
        var (guard, inner, sink) = Create(new SpendSnapshot(4m, "balance", 1m, HasData: true), Enabled);

        using (CallPurposeScope.Begin(CallPurpose.MailDigest))
        {
            await guard.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }

        Assert.Equal(1, inner.Calls);
        Assert.Contains(sink.Raised, a => a.Kind == AlertKind.QuotaWarn);
    }
}
