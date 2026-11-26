using Serilog;
using ZipperAIQQ.Server.Douyin;
using ZipperAIQQ.Storage.Douyin;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 写闸门（G3）：24 小时滚动配额 / 最小间隔 / 同人一次 / 拒绝也要留痕。
/// </summary>
/// <remarks>
/// 用假仓储 + 注入的 <c>now</c>：闸门的价值全在边界上（第 24 小时的第 1 秒算不算、
/// 间隔差 1 毫秒算不算），靠真时钟测这些等于碰运气。
/// </remarks>
public sealed class WriteGateTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static WriteQuota Quota(int perDay = 3, int minIntervalMs = 30_000, bool oncePerTarget = true) =>
        new("commentReply", perDay, minIntervalMs, oncePerTarget);

    [Fact]
    public async Task Allows_when_nothing_was_written_before()
    {
        var gate = new WriteGate(new FakeStore(), Logger);

        var decision = await gate.CheckAsync(Quota(), "a:甲", Now);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public async Task Denies_when_the_daily_quota_is_used_up_and_says_when_it_comes_back()
    {
        var store = new FakeStore();
        for (var i = 0; i < 3; i += 1)
        {
            // 三条分别在 1 / 2 / 3 小时前 ⇒ 最早那条决定配额什么时候回血
            store.Seed("commentReply", $"a:第{i}人", Now.AddHours(-1 - i));
        }

        var gate = new WriteGate(store, Logger);
        var decision = await gate.CheckAsync(Quota(), "a:甲", Now);

        Assert.False(decision.Allowed);
        Assert.Contains("3/3", decision.Reason!, StringComparison.Ordinal);

        // 最早那条 3 小时前写的 ⇒ 还要等 21 小时才回血
        Assert.NotNull(decision.RetryAfter);
        Assert.Equal(21, Math.Round(decision.RetryAfter!.Value.TotalHours));
    }

    [Fact]
    public async Task Rows_outside_the_window_do_not_count()
    {
        var store = new FakeStore();
        for (var i = 0; i < 3; i += 1)
        {
            store.Seed("commentReply", $"a:第{i}人", Now.AddHours(-25));
        }

        var gate = new WriteGate(store, Logger);

        Assert.True((await gate.CheckAsync(Quota(), "a:甲", Now)).Allowed);
    }

    [Fact]
    public async Task Denies_when_the_last_write_was_too_recent()
    {
        // 上一次是 5 秒前、要求至少 30 秒：不能"再试一次就过了"
        var store = new FakeStore();
        store.Seed("commentReply", "a:乙", Now.AddSeconds(-5));

        var gate = new WriteGate(store, Logger);
        var decision = await gate.CheckAsync(Quota(), "a:甲", Now);

        Assert.False(decision.Allowed);
        Assert.Contains("30 秒", decision.Reason!, StringComparison.Ordinal);
        Assert.NotNull(decision.RetryAfter);
        Assert.Equal(25, Math.Round(decision.RetryAfter!.Value.TotalSeconds));
    }

    [Fact]
    public async Task Denies_the_same_target_once_per_window()
    {
        var store = new FakeStore();
        store.Seed("commentReply", "a:甲", Now.AddHours(-2));

        var gate = new WriteGate(store, Logger);
        var decision = await gate.CheckAsync(Quota(), "a:甲", Now);

        Assert.False(decision.Allowed);
        Assert.Contains("已经回过", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Once_per_target_can_be_turned_off()
    {
        var store = new FakeStore();
        store.Seed("commentReply", "a:甲", Now.AddMinutes(-1));

        var gate = new WriteGate(store, Logger);
        var decision = await gate.CheckAsync(Quota(oncePerTarget: false, minIntervalMs: 0), "a:甲", Now);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Zero_per_day_means_off()
    {
        var gate = new WriteGate(new FakeStore(), Logger);

        var decision = await gate.CheckAsync(Quota(perDay: 0), "a:甲", Now);

        Assert.False(decision.Allowed);
        Assert.Contains("等于关闭", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Records_carry_action_target_and_outcome()
    {
        var store = new FakeStore();
        var gate = new WriteGate(store, Logger);

        await gate.RecordAsync(Quota(), "a:甲", "7706", "7412", ok: false, note: "denied:配额", Now);

        var row = Assert.Single(store.Rows);
        Assert.Equal("commentReply", row.Action);
        Assert.Equal("a:甲", row.TargetKey);
        Assert.False(row.Ok);
        Assert.Equal("denied:配额", row.Note);
        Assert.Equal(Now, row.At);
    }

    [Fact]
    public async Task Record_failure_never_breaks_the_caller()
    {
        // 记账有洞要响亮（Error 日志），但不能把"已经发出去了"变成"失败"
        var gate = new WriteGate(new FakeStore { ThrowOnAppend = true }, Logger);

        await gate.RecordAsync(Quota(), "a:甲", null, null, ok: true, note: "readback", Now);
    }

    [Fact]
    public async Task Record_trims_the_note_to_one_short_line()
    {
        var store = new FakeStore();
        var gate = new WriteGate(store, Logger);

        await gate.RecordAsync(
            Quota(), "a:甲", null, null, ok: false, note: "第一行\n第二行" + new string('长', 300), Now);

        var note = Assert.Single(store.Rows).Note!;
        Assert.DoesNotContain('\n', note);
        Assert.True(note.Length <= 121, $"备注没压住：{note.Length}");
    }

    /// <summary>内存仓储：只实现闸门用到的四个查询。</summary>
    private sealed class FakeStore : IDouyinWriteStore
    {
        public List<DouyinWriteAction> Rows { get; } = [];

        public bool ThrowOnAppend { get; init; }

        public void Seed(string action, string targetKey, DateTimeOffset at, bool ok = true) =>
            Rows.Add(new DouyinWriteAction(action, targetKey, ok, at));

        public Task<long> AppendAsync(DouyinWriteAction action, CancellationToken ct = default)
        {
            if (ThrowOnAppend)
            {
                throw new InvalidOperationException("库被锁了");
            }

            Rows.Add(action);
            return Task.FromResult((long)Rows.Count);
        }

        public Task<int> CountSinceAsync(
            string action, DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult(Rows.Count(row => row.Action == action && row.At >= since));

        public Task<DateTimeOffset?> OldestSinceAsync(
            string action, DateTimeOffset since, CancellationToken ct = default)
        {
            var oldest = Rows
                .Where(row => row.Action == action && row.At >= since)
                .Select(row => (DateTimeOffset?)row.At)
                .Min();

            return Task.FromResult(oldest);
        }

        public Task<DateTimeOffset?> LastAtAsync(string action, CancellationToken ct = default)
        {
            var last = Rows
                .Where(row => row.Action == action)
                .Select(row => (DateTimeOffset?)row.At)
                .Max();

            return Task.FromResult(last);
        }

        public Task<bool> ExistsForTargetAsync(
            string action, string targetKey, DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult(Rows.Any(row =>
                row.Action == action && row.TargetKey == targetKey && row.At >= since && row.Ok));

        public Task<IReadOnlyList<DouyinWriteAction>> ListRecentAsync(
            int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DouyinWriteAction>>(
                Rows.OrderByDescending(row => row.At).Take(limit).ToList());
    }
}
