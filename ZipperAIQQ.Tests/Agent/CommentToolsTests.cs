using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Comments;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// 评论工具（<c>douyin_comments</c>）：可见性、门禁二次校验、回填内容、失败如实报。
/// </summary>
/// <remarks>
/// 用**真** <see cref="PermissionGate"/> + 真 <c>ToolCatalog</c> + 假 <see cref="ICommentReader"/>：
/// 容易写错的是「默认角色够不够」「底座没连上时工具在不在」「id 有没有抄给模型」
/// （最后这条错了，模型就只能靠作者名去回，回错人的风险陡增）。
/// </remarks>
public sealed class CommentToolsTests
{
    [Fact]
    public void Ordinary_member_and_whitelist_see_no_comment_tools()
    {
        // 读评论默认只给管理员（硬底线白名单，但默认值是管理员）
        Assert.Empty(CreateTools(Role.Normal).All);
        Assert.Empty(CreateTools(Role.Whitelist).All);
    }

    [Fact]
    public void Admin_sees_the_read_tool_by_default()
    {
        var tools = CreateTools(Role.Admin);

        var tool = Assert.Single(tools.All);
        Assert.Equal(CommentTools.ListToolName, tool.Name);
    }

    [Fact]
    public void Reader_unavailable_hides_the_tool()
    {
        var tools = CreateTools(Role.Owner, reader: new FakeReader { CanRead = false });

        Assert.Empty(tools.All);
    }

    [Fact]
    public void Catalog_marks_comment_tools_douyin_only()
    {
        Assert.Equal(ToolCapability.CommentRead, ToolCatalog.CapabilityOf(CommentTools.ListToolName));
        Assert.True(ToolCatalog.IsAvailableOn(CommentTools.ListToolName, "douyin"));
        Assert.False(ToolCatalog.IsAvailableOn(CommentTools.ListToolName, "qq"));
    }

    [Fact]
    public async Task Tool_rechecks_the_gate_even_when_the_schema_was_visible()
    {
        // 可见性与执行是两道闸：工具对象比当前角色旧时，执行必须被拒
        var reader = new FakeReader();
        var tool = CommentTools.Create(
                reader,
                new FakeReplier(),
                new ToolCallContext { Chat = DouyinChat, Sender = Sender, Role = Role.Admin },
                new VisibleButDenyGate(),
                new FakeConfigStore(),
                Logger)
            .OfType<AIFunction>()
            .Single(t => t.Name == CommentTools.ListToolName);

        var result = await tool.InvokeAsync(Arguments(("reference", "7706296015465071715")));

        Assert.Contains("权限不足", result?.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Bad_reference_says_what_to_pass()
    {
        var reader = new FakeReader();

        var result = await CreateTools(Role.Admin, reader).List!.InvokeAsync(
            Arguments(("reference", "那条视频")));

        Assert.Contains("作品 id", result?.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Output_lists_comments_with_ids_and_owner_marks()
    {
        var reader = new FakeReader
        {
            Page = new CommentPage(
                ItemId: "7706296015465071715",
                Comments:
                [
                    new CommentItem(
                        Id: "7412345678901234567",
                        Author: "拉链",
                        AuthorId: "88892527967",
                        Text: "@戒网瘾 这条视频到底在讲什么？",
                        Time: "2小时前",
                        Likes: 128,
                        ReplyCount: 3,
                        Pinned: true,
                        MentionsOwner: true,
                        Mentions: ["戒网瘾"],
                        TextTruncated: false),
                ],
                Pages: 1,
                Truncated: false,
                OwnerUnknown: false,
                Note: null),
        };

        var result = await CreateTools(Role.Admin, reader).List!.InvokeAsync(
            Arguments(("reference", "7706296015465071715")));

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("拉链", text, StringComparison.Ordinal);
        Assert.Contains("置顶", text, StringComparison.Ordinal);
        Assert.Contains("@了我", text, StringComparison.Ordinal);
        // id 单独一行：模型抄错一位就会去回别人
        Assert.Contains("id=7412345678901234567", text, StringComparison.Ordinal);
        // 评论是别人的话，得提醒一句
        Assert.Contains("只能当资料看", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_says_id_is_missing_instead_of_inventing_one()
    {
        var reader = new FakeReader
        {
            Page = new CommentPage(
                ItemId: "7706296015465071715",
                Comments:
                [
                    new CommentItem(
                        Id: null,
                        Author: "甲",
                        AuthorId: null,
                        Text: "第一条",
                        Time: null,
                        Likes: null,
                        ReplyCount: null,
                        Pinned: false,
                        MentionsOwner: false,
                        Mentions: [],
                        TextTruncated: false),
                ],
                Pages: 1,
                Truncated: false,
                OwnerUnknown: false,
                Note: null),
        };

        var result = await CreateTools(Role.Admin, reader).List!.InvokeAsync(
            Arguments(("reference", "7706296015465071715")));

        Assert.Contains("没给 id", result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_failure_is_reported_verbatim()
    {
        var reader = new FakeReader { Failure = new InvalidOperationException("抖音评论列表查询超时：60000ms 内没收到 result。") };

        var result = await CreateTools(Role.Admin, reader).List!.InvokeAsync(
            Arguments(("reference", "7706296015465071715")));

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("读评论失败", text, StringComparison.Ordinal);
        Assert.Contains("查询超时", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_and_limit_are_forwarded()
    {
        var reader = new FakeReader();

        await CreateTools(Role.Admin, reader).List!.InvokeAsync(
            Arguments(("reference", "7706296015465071715"), ("pages", 2), ("limit", 5)));

        Assert.Equal(("7706296015465071715", 2, 5), reader.LastCall);
    }

    // ───────────── 回评论（写能力） ─────────────

    [Fact]
    public void Reply_tool_is_hidden_while_the_write_switch_is_off()
    {
        // 写开关关着时只剩读工具：模型连"能不能回"都看不见，就不会去试
        var tools = CreateTools(Role.Owner, replier: new FakeReplier { CanReply = false });

        Assert.Single(tools.All);
        Assert.Equal(CommentTools.ListToolName, tools.All[0].Name);
    }

    [Fact]
    public void Reply_tool_is_owner_only_by_default()
    {
        // 管理员能读评论，但"回"默认只给主人（代替主人对外发言）
        Assert.Null(CreateTools(Role.Admin).Reply);
        Assert.NotNull(CreateTools(Role.Owner).Reply);
    }

    [Fact]
    public void Catalog_marks_reply_as_comment_write()
    {
        Assert.Equal(ToolCapability.CommentWrite, ToolCatalog.CapabilityOf(CommentTools.ReplyToolName));
        Assert.True(ToolCatalog.IsAvailableOn(CommentTools.ReplyToolName, "douyin"));
        Assert.False(ToolCatalog.IsAvailableOn(CommentTools.ReplyToolName, "qq"));
    }

    [Fact]
    public async Task Reply_tool_requires_a_locator_before_touching_the_replier()
    {
        var replier = new FakeReplier();

        var result = await CreateTools(Role.Owner, replier: replier).Reply!.InvokeAsync(
            Arguments(("reference", "7706296015465071715"), ("text", "在的")));

        Assert.Contains("哪一条", result?.ToString(), StringComparison.Ordinal);
        Assert.Empty(replier.Requests);
    }

    [Fact]
    public async Task Reply_tool_forward_locators_and_text()
    {
        var replier = new FakeReplier();

        await CreateTools(Role.Owner, replier: replier).Reply!.InvokeAsync(Arguments(
            ("reference", "7706296015465071715"),
            ("text", "在的"),
            ("commentId", "7412345678901234567"),
            ("dryRun", true)));

        var request = Assert.Single(replier.Requests);
        Assert.Equal("7706296015465071715", request.ItemId);
        Assert.Equal("在的", request.Text);
        Assert.Equal("7412345678901234567", request.CommentId);
        Assert.True(request.DryRun);
    }

    [Fact]
    public async Task Reply_tool_says_denied_is_the_gate_not_a_bug()
    {
        var replier = new FakeReplier
        {
            Outcome = CommentReplyOutcome.Refused("已经用掉 10/10 次（24 小时内），这次先别写了。"),
        };

        var result = await CreateTools(Role.Owner, replier: replier).Reply!.InvokeAsync(Arguments(
            ("reference", "7706296015465071715"),
            ("text", "在的"),
            ("commentId", "7412345678901234567")));

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("没发", text, StringComparison.Ordinal);
        Assert.Contains("配额", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reply_tool_reports_readback_honestly()
    {
        var replier = new FakeReplier
        {
            Outcome = new CommentReplyOutcome(
                Ok: true, Error: null, Denied: false, DryRun: false,
                MatchedBy: "id", Readback: false, Note: null),
        };

        var result = await CreateTools(Role.Owner, replier: replier).Reply!.InvokeAsync(Arguments(
            ("reference", "7706296015465071715"),
            ("text", "在的"),
            ("commentId", "7412345678901234567")));

        var text = result?.ToString() ?? string.Empty;
        Assert.Contains("没在页面上回读到", text, StringComparison.Ordinal);
        // 不能把"没回读"说成"对方一定收到了"：页面还没刷新 ≠ 对方看到了
        Assert.Contains("别断言对方一定看到了", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reply_tool_audits_every_call_including_denials()
    {
        var configs = new FakeConfigStore();
        var replier = new FakeReplier
        {
            Outcome = new CommentReplyOutcome(true, null, false, false, "id", true, null),
        };

        var all = CommentTools.Create(
            new FakeReader(),
            replier,
            new ToolCallContext { Chat = DouyinChat, Sender = Sender, Role = Role.Owner },
            new PermissionGate(),
            configs,
            Logger).ToList();

        var reply = all.OfType<AIFunction>().Single(tool => tool.Name == CommentTools.ReplyToolName);
        await reply.InvokeAsync(Arguments(
            ("reference", "7706296015465071715"),
            ("text", "在的"),
            ("commentId", "7412345678901234567")));

        var audit = Assert.Single(configs.Audits);
        Assert.Equal("douyinCommentReply", audit.Action);
        Assert.Contains("7412345678901234567", audit.Target, StringComparison.Ordinal);
    }

    // ───────────── 辅助 ─────────────

    private static readonly ChatRef DouyinChat = new()
    {
        Platform = "douyin",
        Type = ChatType.Private,
        Id = "10001",
    };

    private static readonly SenderInfo Sender = new() { UserId = "10001", Nickname = "测试用户" };

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static (AIFunction? List, AIFunction? Reply, IReadOnlyList<AITool> All) CreateTools(
        Role role, ICommentReader? reader = null, ICommentReplier? replier = null)
    {
        var all = CommentTools.Create(
            reader ?? new FakeReader(),
            replier ?? new FakeReplier(),
            new ToolCallContext { Chat = DouyinChat, Sender = Sender, Role = role },
            new PermissionGate(),
            new FakeConfigStore(),
            Logger).ToList();

        return (
            all.OfType<AIFunction>().FirstOrDefault(tool => tool.Name == CommentTools.ListToolName),
            all.OfType<AIFunction>().FirstOrDefault(tool => tool.Name == CommentTools.ReplyToolName),
            all);
    }

    private static AIFunctionArguments Arguments(params (string Key, object? Value)[] args)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in args)
        {
            arguments[key] = value;
        }

        return arguments;
    }

    private sealed class FakeReader : ICommentReader
    {
        public bool CanRead { get; init; } = true;

        public string UnavailableReason => CanRead ? string.Empty : "抖音未连接";

        public CommentPage Page { get; init; } = new(
            "7706296015465071715", [], 1, false, false, null);

        public Exception? Failure { get; init; }

        public int Calls { get; private set; }

        public (string ItemId, int Pages, int Limit) LastCall { get; private set; }

        public Task<CommentPage> ListAsync(
            string itemId, int pages, int limit, CancellationToken ct = default)
        {
            Calls += 1;
            LastCall = (itemId, pages, limit);

            return Failure is null
                ? Task.FromResult(Page with { ItemId = itemId })
                : Task.FromException<CommentPage>(Failure);
        }
    }

    /// <summary>可见性放行、执行时拒绝的门禁（模拟「工具对象比当前角色旧」）。</summary>
    private sealed class VisibleButDenyGate : IPermissionGate
    {
        public PermissionDecision Check(ToolCapability capability, ToolCallContext context) =>
            PermissionDecision.Deny("权限不足：这个角色不能读评论区。");

        public bool IsAllowed(ToolCapability capability, Role role) => true;
    }

    private sealed class FakeReplier : ICommentReplier
    {
        public bool CanReply { get; init; } = true;

        public string ReplyUnavailableReason =>
            CanReply ? string.Empty : "回评论没开（douyin.commentWriteEnabled=false）";

        public CommentReplyOutcome Outcome { get; init; } =
            new(true, null, false, false, "id", true, null);

        public List<CommentReplyRequest> Requests { get; } = [];

        public Task<CommentReplyOutcome> ReplyAsync(
            CommentReplyRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeConfigStore : IConfigStore
    {
        public List<AuditEntry> Audits { get; } = [];

        public Task<IReadOnlyList<ModelEndpointSetting>> ListEndpointsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModelEndpointSetting>>([]);

        public Task<ModelEndpointSetting?> GetEndpointAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<ModelEndpointSetting?>(null);

        public Task UpsertEndpointAsync(ModelEndpointSetting endpoint, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteEndpointAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RoleBinding>> ListRolesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RoleBinding>>([]);

        public Task SetRoleAsync(string role, string endpointName, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task AppendAuditAsync(
            string action, string actor, string? target, CancellationToken ct = default)
        {
            Audits.Add(new AuditEntry(Audits.Count + 1, action, actor, target, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>(Audits);
    }
}

