using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Commands;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// <c>/status</c> 的 JEV 一行：**配置状态**与**调用次数**必须分开
/// （问题记录 04 #2 —— 旧实现把「24 小时没调用」写成「未启用」，误导排障）。
/// </summary>
public sealed class StatusCommandTests
{
    // ── 桩 ──

    private sealed class FakeMetrics(params MetricSummary[] rows) : IMetricsStore
    {
        public Task RecordAsync(MetricEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MetricSummary>> GetSummaryAsync(
            int windowHours = 24, string? sessionId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetricSummary>>(rows);

        public Task<IReadOnlyList<MetricSessionInfo>> ListSessionAsync(
            int windowHours = 24, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetricSessionInfo>>([]);
    }

    /// <summary>只关心 <see cref="CountAsync"/>，其余成员本测试用不到。</summary>
    private sealed class FakeMessages(long count = 7) : IMessageStore
    {
        public Task<long> CountAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(count);

        public Task<long> AppendAsync(IncomingMessage message, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> AppendOutgoingAsync(
            OutgoingMessage message, string senderUserId, DateTimeOffset sentAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<long> AppendCommandAsync(
            OutgoingMessage message, string senderUserId, DateTimeOffset sentAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMessage>> GetRecentAsync(
            string sessionId, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMessage>> GetRangeAsync(
            string sessionId, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMessage>> GetByIdsAsync(
            string sessionId, IReadOnlyList<long> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredMessage?> FindByMessageIdAsync(
            string sessionId, string messageId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMessage>> SearchAsync(
            string sessionId, string keyword, int limit, DateTimeOffset? from, DateTimeOffset? to,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMessage>> SearchAcrossSessionsAsync(
            string keyword, int limit, string? platform, string? sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
            int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> PurgeAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReplyModes : IReplyModeDecider
    {
        private readonly ReplyMode _mode;
        private readonly Exception? _failure;

        private FakeReplyModes(ReplyMode mode, Exception? failure)
        {
            _mode = mode;
            _failure = failure;
        }

        public static FakeReplyModes Of(ReplyMode mode) => new(mode, null);
        public static FakeReplyModes Failing(Exception ex) => new(ReplyMode.Every, ex);

        public Task<ReplyMode> GetModeAsync(string sessionId, CancellationToken ct = default) =>
            _failure is not null ? Task.FromException<ReplyMode>(_failure) : Task.FromResult(_mode);

        public Task<bool> ShouldReplyAsync(
            IncomingMessage message, string selfId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public bool IsMentioned(IncomingMessage message, string selfId) => true;
    }

    private sealed class FakeClients : IChatClientProvider
    {
        public bool HasClient => false;
        public IChatClient Current => throw new NotSupportedException();
        public string? CurrentModel => "deepseek-chat";
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── 构造 ──

    private static StatusCommand Command(
        IReplyModeDecider modes,
        params MetricSummary[] rows) =>
        new(
            new FakeMetrics(rows),
            new FakeMessages(),
            new ServerOptions
            {
                NapCatWebSocketUrl = "ws://127.0.0.1:1",
                LlmApiKey = "test",
                LlmBaseUrl = "https://api.example",
                LlmModel = "deepseek-chat",
            },
            new FakeClients(),
            modes);

    private static CommandContext Context() => new(
        new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "1" },
        new SenderInfo { UserId = "1" },
        Role.Normal,
        DateTimeOffset.UtcNow);

    private static MetricSummary Jev(long calls, long replies) =>
        new(MetricKindsForTest.JevJudge, calls, 12, 10, 20, replies, 0);

    // ══════ 用例 ══════

    [Fact]
    public async Task Jev_enabled_but_zero_calls_is_not_reported_as_disabled()
    {
        var text = await Command(FakeReplyModes.Of(ReplyMode.Jev))
            .ExecuteAsync(Context(), string.Empty);

        Assert.Contains("JEV 判断：0 次（已启用 · jev）", text);
        Assert.DoesNotContain("未启用", text);
    }

    [Fact]
    public async Task Jev_enabled_with_calls_shows_counts()
    {
        var text = await Command(FakeReplyModes.Of(ReplyMode.Jev), Jev(5, 3))
            .ExecuteAsync(Context(), string.Empty);

        Assert.Contains("JEV 判断：5 次 · 回 3 · 不回 2", text);
        Assert.DoesNotContain("未启用", text);
    }

    [Fact]
    public async Task Jev_disabled_names_the_current_mode()
    {
        var text = await Command(FakeReplyModes.Of(ReplyMode.Mention))
            .ExecuteAsync(Context(), string.Empty);

        Assert.Contains("JEV 判断：未启用（当前 mention）", text);
    }

    [Fact]
    public async Task Jev_config_read_failure_is_a_third_state()
    {
        var text = await Command(FakeReplyModes.Failing(new InvalidOperationException("boom")))
            .ExecuteAsync(Context(), string.Empty);

        Assert.Contains("JEV 判断：读取失败", text);
        Assert.DoesNotContain("未启用", text);
    }

    [Fact]
    public async Task No_metrics_still_shows_the_config_state()
    {
        var text = await Command(FakeReplyModes.Of(ReplyMode.Jev))
            .ExecuteAsync(Context(), string.Empty);

        Assert.Contains("近 24 小时：无调用记录", text);
        Assert.Contains("0 次（已启用 · jev）", text);
    }

    [Fact]
    public async Task Elapsed_shows_p95_next_to_average()
    {
        var text = await Command(
                FakeReplyModes.Of(ReplyMode.Every),
                new MetricSummary("llm.call", 4, 120, 100, 300, 1000, 200, 0, 110, 290))
            .ExecuteAsync(Context(), string.Empty);

        Assert.Contains("单次平均 120ms · p95 290ms", text);
    }

    [Fact]
    public async Task Turn_total_row_appears_when_recorded()
    {
        // 整轮（含工具循环）与单次往返分开展示，问题记录 04 #3 的口径争议就落在这。
        var text = await Command(
                FakeReplyModes.Of(ReplyMode.Every),
                new MetricSummary("turn.total", 2, 8000, 7000, 9000, 0, 0, 0, 7500, 8900))
            .ExecuteAsync(Context(), string.Empty);

        Assert.Contains("整轮耗时：2 轮（平均 8000ms · p95 8900ms）", text);
    }

    [Fact]
    public async Task Category_rows_use_chinese_labels()
    {
        var text = await Command(
                FakeReplyModes.Of(ReplyMode.Every),
                new MetricSummary("llm.call", 2, 100, 90, 120, 10, 5),
                new MetricSummary("tool.send_message", 3, 20, 10, 30, 0, 0))
            .ExecuteAsync(Context(), string.Empty);

        Assert.Contains("· LLM 调用：2 次", text);
        Assert.Contains("· 发送消息：3 次", text);
        Assert.DoesNotContain("llm.call", text);
        Assert.DoesNotContain("tool.send_message", text);
    }

    [Fact]
    public async Task Cache_hit_rate_is_reported()
    {
        // 缓存命中 = 命中缓存的输入 token / 输入 token 总数（影响费用与首字延迟）
        var text = await Command(
                FakeReplyModes.Of(ReplyMode.Every),
                new MetricSummary("llm.call", 2, 100, 90, 120, 1000, 200, 600))
            .ExecuteAsync(Context(), string.Empty);

        Assert.Contains("缓存命中率：60%", text);
        Assert.Contains("缓存 600", text);
    }

    /// <summary>与产品常量分开写：测试要能发现「改了一个名字却忘了改另一处」。</summary>
    private static class MetricKindsForTest
    {
        public const string JevJudge = "jev.judge";
    }
}
