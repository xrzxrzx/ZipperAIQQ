using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Comments;
using ZipperAIQQ.Server.Channels;
using ZipperAIQQ.Server.Douyin;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 抖音评论服务（B5）：条数/页数夹取、正文截断、`@主人` 三态判定、不可用时的行为。
/// </summary>
/// <remarks>
/// 用假 <see cref="IDouyinCommentSource"/> + 假 <see cref="IOwnerLookup"/>：
/// 这里要证明的是**服务端的口径**（夹取、截断、三态、失败如实报），
/// 不是浏览器能不能读到评论（那是真机验收的事）。
/// </remarks>
public sealed class DouyinCommentServiceTests
{
    private const string ItemId = "7706296015465071715";

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task List_marks_mentions_of_owner()
    {
        var source = new FakeSource
        {
            List = new DouyinCommentList(ItemId, [
                Comment("甲", "这视频真不错", mentions: []),
                Comment("乙", "@拉链 你怎么看", mentions: ["拉链"]),
            ], Pages: 1, Truncated: false, Note: null),
        };

        var page = await CreateService(source, ownerName: "拉链").ListAsync(ItemId, 0, 0);

        Assert.False(page.OwnerUnknown);
        Assert.False(page.Comments[0].MentionsOwner);
        Assert.True(page.Comments[1].MentionsOwner);
    }

    [Fact]
    public async Task List_reports_mentions_unknown_when_owner_nickname_is_missing()
    {
        // 判不了就给 null（不知道），不给 false（确认没 @ 我）—— 两者会让模型做出完全不同的决定
        var source = new FakeSource
        {
            List = new DouyinCommentList(ItemId, [Comment("乙", "@拉链 在不在", mentions: ["拉链"])],
                Pages: 1, Truncated: false, Note: null),
        };

        var page = await CreateService(source, ownerName: null).ListAsync(ItemId, 0, 0);

        Assert.True(page.OwnerUnknown);
        Assert.Null(page.Comments[0].MentionsOwner);
        Assert.Contains("主人昵称", page.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_works_without_owner_lookup_at_all()
    {
        var source = new FakeSource
        {
            List = new DouyinCommentList(ItemId, [Comment("甲", "只有一条", mentions: [])],
                Pages: 1, Truncated: false, Note: null),
        };

        var page = await CreateService(source, ownerName: null, owner: null).ListAsync(ItemId, 0, 0);

        Assert.True(page.OwnerUnknown);
        Assert.Single(page.Comments);
    }

    [Fact]
    public async Task List_truncates_long_comments_and_flags_it()
    {
        var source = new FakeSource
        {
            List = new DouyinCommentList(
                ItemId,
                [Comment("甲", new string('长', 5000), mentions: [])],
                Pages: 1,
                Truncated: false,
                Note: null),
        };

        var page = await CreateService(source, ownerName: "拉链").ListAsync(ItemId, 0, 0);

        var comment = page.Comments[0];
        Assert.True(comment.TextTruncated);
        Assert.Equal(501, comment.Text.Length); // 500 字 + 省略号
        Assert.EndsWith("…", comment.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_clamps_pages_and_limit_to_the_hard_caps()
    {
        var source = new FakeSource();
        var service = CreateService(source, ownerName: "拉链");

        await service.ListAsync(ItemId, pages: 999, limit: 999);

        Assert.Equal((10, 100), (source.LastPages, source.LastLimit));
    }

    [Fact]
    public async Task List_uses_configured_defaults_when_model_passes_zero()
    {
        var source = new FakeSource();
        var service = CreateService(
            source,
            ownerName: "拉链",
            options: new DouyinCommentOptions { MaxPages = 2, MaxComments = 7 });

        await service.ListAsync(ItemId, pages: 0, limit: 0);

        Assert.Equal((2, 7), (source.LastPages, source.LastLimit));
    }

    [Fact]
    public async Task List_says_so_when_everything_was_cut_by_the_limit()
    {
        var source = new FakeSource
        {
            List = new DouyinCommentList(ItemId, [Comment("甲", "一", mentions: []), Comment("乙", "二", mentions: [])],
                Pages: 1, Truncated: false, Note: null),
        };

        var page = await CreateService(source, ownerName: "拉链").ListAsync(ItemId, pages: 1, limit: 2);

        Assert.True(page.Truncated);
    }

    [Fact]
    public async Task List_refuses_to_pretend_when_the_base_is_missing()
    {
        var source = new FakeSource { Ready = false, NotReady = "抖音未连接：浏览器底座还没连上" };

        var service = CreateService(source, ownerName: "拉链");

        Assert.False(service.CanRead);
        Assert.Contains("浏览器底座", service.UnavailableReason, StringComparison.Ordinal);

        // 不可用时**抛**，不返回空列表冒充「这条视频没有评论」
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAsync(ItemId, 0, 0));
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task List_honors_the_switch()
    {
        var source = new FakeSource();
        var service = CreateService(
            source, ownerName: "拉链", options: new DouyinCommentOptions { Enabled = false });

        Assert.False(service.CanRead);
        Assert.Contains("commentEnabled", service.UnavailableReason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAsync(ItemId, 0, 0));
    }

    [Fact]
    public async Task List_notes_an_empty_comment_area()
    {
        var source = new FakeSource
        {
            List = new DouyinCommentList(ItemId, [], Pages: 1, Truncated: false, Note: null),
        };

        var page = await CreateService(source, ownerName: "拉链").ListAsync(ItemId, 0, 0);

        Assert.Empty(page.Comments);
        Assert.Contains("没读到评论", page.Note!, StringComparison.Ordinal);
    }

    // ───────────── 回评论（写闸门） ─────────────

    [Fact]
    public async Task Reply_is_off_until_the_write_switch_is_on()
    {
        var source = new FakeSource();
        var service = CreateService(
            source, ownerName: "拉链", options: new DouyinCommentOptions { WriteEnabled = false });

        Assert.False(service.CanReply);
        Assert.Contains("commentWriteEnabled", service.ReplyUnavailableReason, StringComparison.Ordinal);

        var outcome = await service.ReplyAsync(
            new CommentReplyRequest(ItemId, "在的", CommentId: "7412345678901234567"));

        Assert.False(outcome.Ok);
        Assert.False(outcome.Denied);
        Assert.Null(source.LastReply);
    }

    [Fact]
    public async Task Reply_records_the_gate_denial_instead_of_silently_dropping_it()
    {
        var source = new FakeSource();
        var gate = new FakeGate
        {
            Decision = WriteDecision.Deny("已经用掉 10/10 次（24 小时内），这次先别写了。"),
        };

        var service = CreateService(source, ownerName: "拉链", gate: gate);
        var outcome = await service.ReplyAsync(
            new CommentReplyRequest(ItemId, "在的", CommentId: "7412345678901234567"));

        Assert.False(outcome.Ok);
        Assert.True(outcome.Denied);
        Assert.Contains("10/10", outcome.Error!, StringComparison.Ordinal);

        // 被拒也要留痕，且**不能**真的去打底座
        Assert.Null(source.LastReply);
        var record = Assert.Single(gate.Records);
        Assert.False(record.Ok);
        Assert.StartsWith("denied:", record.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reply_passes_locators_and_records_the_success()
    {
        var source = new FakeSource();
        var gate = new FakeGate();

        var service = CreateService(source, ownerName: "拉链", gate: gate);
        var outcome = await service.ReplyAsync(new CommentReplyRequest(
            ItemId, " 在的 ", CommentId: "7412345678901234567", DryRun: false));

        Assert.True(outcome.Ok);
        Assert.True(outcome.Readback);
        Assert.NotNull(source.LastReply);
        Assert.Equal("在的", source.LastReply!.Text);
        Assert.True(source.LastReply.Commit);
        Assert.Equal("7412345678901234567", source.LastReply.CommentId);

        var record = Assert.Single(gate.Records);
        Assert.True(record.Ok);
        Assert.Equal("readback", record.Note);
    }

    [Fact]
    public async Task Reply_target_key_prefers_the_author_for_once_per_person()
    {
        var gate = new FakeGate();

        var service = CreateService(new FakeSource(), ownerName: "拉链", gate: gate);
        await service.ReplyAsync(new CommentReplyRequest(
            ItemId, "在的", CommentId: "7412345678901234567", Author: " 拉链 "));

        Assert.Equal("a:拉链", Assert.Single(gate.Records).Target);
    }

    [Fact]
    public async Task Reply_dry_run_skips_the_gate_and_never_commits()
    {
        var source = new FakeSource();
        var gate = new FakeGate();

        var service = CreateService(source, ownerName: "拉链", gate: gate);
        var outcome = await service.ReplyAsync(new CommentReplyRequest(
            ItemId, "在的", CommentId: "7412345678901234567", DryRun: true));

        Assert.True(outcome.Ok);
        Assert.True(outcome.DryRun);
        // 干跑没有对外动作 ⇒ 不占配额、不查闸门、不留写记录
        Assert.Equal(0, gate.Checks);
        Assert.Empty(gate.Records);
        Assert.False(source.LastReply!.Commit);
    }

    [Fact]
    public async Task Reply_requires_a_locator_and_reasonable_text()
    {
        var service = CreateService(new FakeSource(), ownerName: "拉链");

        var noLocator = await service.ReplyAsync(new CommentReplyRequest(ItemId, "在的"));
        Assert.False(noLocator.Ok);
        Assert.Contains("哪一条", noLocator.Error!, StringComparison.Ordinal);

        var empty = await service.ReplyAsync(
            new CommentReplyRequest(ItemId, "   ", CommentId: "7412345678901234567"));
        Assert.False(empty.Ok);
        Assert.Contains("空的", empty.Error!, StringComparison.Ordinal);

        var tooLong = await service.ReplyAsync(new CommentReplyRequest(
            ItemId, new string('长', 500), CommentId: "7412345678901234567"));
        Assert.False(tooLong.Ok);
        Assert.Contains("太长", tooLong.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reply_reports_base_failure_and_records_it_as_failed()
    {
        var source = new FakeSource
        {
            ReplyFailure = new InvalidOperationException("这一条评论上找不到「回复」按钮（可能被折叠）"),
        };
        var gate = new FakeGate();

        var service = CreateService(source, ownerName: "拉链", gate: gate);
        var outcome = await service.ReplyAsync(
            new CommentReplyRequest(ItemId, "在的", CommentId: "7412345678901234567"));

        Assert.False(outcome.Ok);
        Assert.False(outcome.Denied);
        Assert.Contains("找不到「回复」按钮", outcome.Error!, StringComparison.Ordinal);

        var record = Assert.Single(gate.Records);
        Assert.False(record.Ok);
        Assert.StartsWith("failed:", record.Note, StringComparison.Ordinal);
    }

    // ───────────── 辅助 ─────────────

    private static DouyinCommentItem Comment(
        string author, string text, IReadOnlyList<string> mentions) =>
        new(
            Id: null,
            Author: author,
            AuthorId: null,
            Text: text,
            Time: "1小时前",
            Likes: 1,
            ReplyCount: null,
            Pinned: false,
            Mentions: mentions);

    private static DouyinCommentService CreateService(
        FakeSource source,
        string? ownerName,
        DouyinCommentOptions? options = null,
        IOwnerLookup? owner = null,
        IWriteGate? gate = null) =>
        new(
            source,
            options ?? new DouyinCommentOptions { WriteEnabled = true },
            gate ?? new FakeGate(),
            Logger,
            owner ?? new FakeOwner(ownerName));

    private sealed class FakeSource : IDouyinCommentSource
    {
        public bool Ready { get; init; } = true;

        public string NotReady { get; init; } = "抖音未连接";

        public DouyinCommentList List { get; init; } = new(ItemId, [], 1, false, null);

        public DouyinCommentReplyResult Reply { get; init; } =
            new(ItemId, DryRun: false, MatchedBy: "id", Readback: true, Note: null);

        public Exception? ReplyFailure { get; init; }

        public int Calls { get; private set; }

        public int LastPages { get; private set; }

        public int LastLimit { get; private set; }

        public DouyinCommentReplyRequest? LastReply { get; private set; }

        public bool IsReady => Ready;

        public string NotReadyReason => Ready ? string.Empty : NotReady;

        public Task<DouyinCommentList> ListAsync(
            string itemId, int maxPages, int maxComments, int timeoutMs, CancellationToken ct = default)
        {
            Calls += 1;
            LastPages = maxPages;
            LastLimit = maxComments;
            return Task.FromResult(List with { ItemId = itemId });
        }

        public Task<DouyinCommentReplyResult> ReplyAsync(
            DouyinCommentReplyRequest request, int timeoutMs, CancellationToken ct = default)
        {
            LastReply = request;

            return ReplyFailure is null
                ? Task.FromResult(Reply with { ItemId = request.ItemId, DryRun = !request.Commit })
                : Task.FromException<DouyinCommentReplyResult>(ReplyFailure);
        }
    }

    /// <summary>写闸门假实现：默认放行，可指定拒绝；记录每次记账。</summary>
    private sealed class FakeGate : IWriteGate
    {
        public WriteDecision Decision { get; init; } = WriteDecision.Allow();

        public List<(string Action, string Target, bool Ok, string? Note)> Records { get; } = [];

        public int Checks { get; private set; }

        public Task<WriteDecision> CheckAsync(
            WriteQuota quota, string targetKey, DateTimeOffset now, CancellationToken ct = default)
        {
            Checks += 1;
            return Task.FromResult(Decision);
        }

        public Task RecordAsync(
            WriteQuota quota,
            string targetKey,
            string? itemId,
            string? commentId,
            bool ok,
            string? note,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            Records.Add((quota.Action, targetKey, ok, note));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOwner : IOwnerLookup
    {
        private readonly string? _name;

        public FakeOwner(string? name) => _name = name;

        public Task<(string? OwnerId, string? OwnerName)> GetAsync(
            string platform, CancellationToken ct = default) =>
            Task.FromResult<(string?, string?)>((_name is null ? null : "88892527967", _name));
    }
}
