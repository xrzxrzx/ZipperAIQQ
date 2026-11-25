using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Mail;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// 邮件双通道工具（<c>mail_render</c> / <c>mail_raw</c>）：可见性、门禁、定位、如实回报、留痕。
/// </summary>
/// <remarks>
/// 用**真** <see cref="PermissionGate"/> + 真目录表 + 假 <see cref="IMailReader"/>：
/// 最容易写错的是「默认角色够不够、渲染不可用时工具还在不在」，
/// 这两条必须走真门禁与真 <c>ToolCatalog</c>，不能自己再写一份口径。
/// </remarks>
public sealed class MailToolsTests
{
    private sealed record Call(string Tool, string Reference, int MaxChars, bool Segments);

    // ───────────── 可见性 / 门禁 ─────────────

    [Fact]
    public void Ordinary_member_sees_no_mail_tools()
    {
        var tools = CreateTools(Role.Normal);

        // 邮件是私信级隐私：普通成员连 schema 都不该看到
        Assert.Empty(tools.All);
    }

    [Fact]
    public void Admin_sees_render_but_not_raw_by_default()
    {
        var tools = CreateTools(Role.Admin);

        // 渲染默认管理员可用；原文默认只给主人
        Assert.Single(tools.All);
        Assert.Equal(MailTools.RenderToolName, tools.All[0].Name);
    }

    [Fact]
    public void Owner_sees_both_tools()
    {
        var tools = CreateTools(Role.Owner);

        Assert.Equal(2, tools.All.Count);
        Assert.Contains(tools.All, tool => tool.Name == MailTools.RenderToolName);
        Assert.Contains(tools.All, tool => tool.Name == MailTools.RawToolName);
    }

    [Fact]
    public void Render_unavailable_hides_only_that_tool()
    {
        // 外置渲染器没装（方案 G4 的降级口径）：渲染工具不出现，原文通道照旧
        var tools = CreateTools(Role.Owner, reader: new FakeReader { CanRender = false });

        Assert.Single(tools.All);
        Assert.Equal(MailTools.RawToolName, tools.All[0].Name);
    }

    [Fact]
    public void Catalog_declares_both_capabilities()
    {
        Assert.Equal(ToolCapability.MailRead, ToolCatalog.CapabilityOf(MailTools.RenderToolName));
        Assert.Equal(ToolCapability.MailRaw, ToolCatalog.CapabilityOf(MailTools.RawToolName));
        // 平台无关：QQ / 抖音会话都该出现
        Assert.True(ToolCatalog.IsAvailableOn(MailTools.RenderToolName, "qq"));
        Assert.True(ToolCatalog.IsAvailableOn(MailTools.RenderToolName, "douyin"));
    }

    // ───────────── 渲染通道 ─────────────

    [Fact]
    public async Task Render_success_returns_caption_plus_image_and_audits()
    {
        var reader = new FakeReader
        {
            Hits = [Brief(7)],
            Render = new MailRenderOutcome(
                [new MailRenderImage(0, [1, 2, 3], "image/jpeg", "整封邮件")],
                "（渲染引擎 chromium）",
                null),
        };
        var configs = new FakeConfigStore();

        var contents = await InvokeRenderAsync(
            CreateTools(Role.Owner, reader: reader, configs: configs).Render!,
            ("mail", "7"));

        Assert.Equal(2, contents.Length);
        var caption = Assert.IsType<TextContent>(contents[0]);
        Assert.Contains("渲染完成", caption.Text);
        Assert.Contains("渲染引擎 chromium", caption.Text);

        var image = Assert.IsType<DataContent>(contents[1]);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Data.ToArray());

        Assert.Contains(configs.Audits, item => item.Action == "mailRender" && item.Target!.Contains("id=7"));
    }

    [Fact]
    public async Task Render_failure_is_reported_as_text_not_an_exception()
    {
        var reader = new FakeReader
        {
            Hits = [Brief(7)],
            Render = MailRenderOutcome.Failed("渲染失败：找不到浏览器内核"),
        };

        var contents = await InvokeRenderAsync(
            CreateTools(Role.Owner, reader: reader).Render!, ("mail", "7"));

        var text = Assert.IsType<TextContent>(Assert.Single(contents));
        Assert.Contains("找不到浏览器内核", text.Text);
    }

    [Fact]
    public async Task Ambiguous_reference_lists_candidates_and_does_not_pick_one()
    {
        var reader = new FakeReader
        {
            Hits = [Brief(1, "银行账单"), Brief(2, "银行活动")],
        };

        var contents = await InvokeRenderAsync(
            CreateTools(Role.Owner, reader: reader).Render!, ("mail", "银行"));

        var text = Assert.IsType<TextContent>(Assert.Single(contents));
        Assert.Contains("#1", text.Text);
        Assert.Contains("#2", text.Text);
        // 没替模型挑：一次都没渲染
        Assert.Empty(reader.Calls);
    }

    [Fact]
    public async Task Segments_flag_is_passed_through()
    {
        var reader = new FakeReader
        {
            Hits = [Brief(9)],
            Render = new MailRenderOutcome(
                [new MailRenderImage(0, [1], "image/jpeg", "第 1/2 段")], string.Empty, null),
        };

        await InvokeRenderAsync(
            CreateTools(Role.Owner, reader: reader).Render!, ("mail", "9"), ("segments", true));

        Assert.Equal((true, 9L), reader.LastSegments);
    }

    // ───────────── 原文通道 ─────────────

    [Fact]
    public async Task Raw_returns_headers_body_and_truncation_warning()
    {
        var reader = new FakeReader
        {
            Hits = [Brief(3, "账单")],
            Raw = new MailRawOutcome(
                "text/html; charset=utf-8",
                "Received: from a by b; Mon, 24 Nov 2026 10:00:00 +0800",
                "<p>验证码 123456</p>",
                Truncated: true,
                Error: null),
        };
        var configs = new FakeConfigStore();

        var text = await InvokeRawAsync(
            CreateTools(Role.Owner, reader: reader, configs: configs).Raw!, ("mail", "3"));

        Assert.Contains("Received: from a by b", text);
        Assert.Contains("<p>验证码 123456</p>", text);
        Assert.Contains("已按上限截断", text);
        Assert.Contains(configs.Audits, item => item.Action == "mailRaw" && item.Target!.Contains("id=3"));
    }

    [Fact]
    public async Task Raw_without_max_chars_passes_zero_so_server_config_wins()
    {
        var reader = new FakeReader { Hits = [Brief(4)], Raw = new MailRawOutcome("", "h", "b", false, null) };

        await CreateTools(Role.Owner, reader: reader).Raw!
            .InvokeAsync(Arguments(("mail", "4")));

        // 0 = 「用服务端配置」：工具里不许再塞一份自己的默认值
        Assert.Equal(0, reader.LastMaxChars);
    }

    [Fact]
    public async Task Raw_reports_missing_mail_without_calling_the_reader()
    {
        var reader = new FakeReader();

        var text = await InvokeRawAsync(
            CreateTools(Role.Owner, reader: reader).Raw!, ("mail", "999"));

        Assert.Contains("没找到", text);
        Assert.Empty(reader.Calls);
    }

    // ───────────── MEAI 透传 ─────────────

    [Fact]
    public async Task Render_images_survive_the_tool_loop_as_data_content()
    {
        // 关键契约：工具返回 AIContent[] 时，MEAI 必须把它当**内容**塞进 FunctionResultContent，
        // 而不是序列化成一段 JSON 文本 —— 否则模型永远看不到那张图。
        var reader = new FakeReader
        {
            Hits = [Brief(11)],
            Render = new MailRenderOutcome(
                [new MailRenderImage(0, [9, 9, 9], "image/jpeg", "整封邮件")], string.Empty, null),
        };

        var tool = CreateTools(Role.Owner, reader: reader).Render!;
        var chat = new ScriptedChatClient();
        var client = new FunctionInvokingChatClient(chat);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "看看这封邮件的版面")],
            new ChatOptions { Tools = [tool] });

        var functionResult = chat.SecondCallMessages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single();

        // 判定口径：结果必须还是**内容对象**（含 DataContent），而不是被序列化成一段 JSON 字符串。
        // 一旦变成字符串，模型就只会读到 base64 或空壳，等于白渲染。
        var contents = Assert.IsAssignableFrom<IEnumerable<AIContent>>(functionResult.Result).ToArray();
        Assert.Contains(
            contents,
            content => content is DataContent data && data.MediaType == "image/jpeg");
    }

    // ───────────── 辅助 ─────────────

    private static readonly ChatRef Chat = new()
    {
        Platform = "qq",
        Type = ChatType.Private,
        Id = "10001",
    };

    private static readonly SenderInfo Sender = new() { UserId = "10001", Nickname = "测试用户" };

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static MailBrief Brief(long id, string subject = "测试邮件") => new(
        id,
        subject,
        "发件人 <sender@example.com>",
        new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(8)),
        HasHtml: true,
        HasUid: true);

    private static (AIFunction? Render, AIFunction? Raw, IReadOnlyList<AITool> All) CreateTools(
        Role role,
        IMailReader? reader = null,
        IConfigStore? configs = null)
    {
        var all = MailTools.Create(
            new ToolCallContext { Chat = Chat, Sender = Sender, Role = role },
            new PermissionGate(),
            reader ?? new FakeReader(),
            configs ?? new FakeConfigStore(),
            Logger).ToList();

        return (
            all.OfType<AIFunction>().FirstOrDefault(tool => tool.Name == MailTools.RenderToolName),
            all.OfType<AIFunction>().FirstOrDefault(tool => tool.Name == MailTools.RawToolName),
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

    private static async Task<AIContent[]> InvokeRenderAsync(
        AIFunction tool, params (string Key, object? Value)[] args)
    {
        var result = await tool.InvokeAsync(Arguments(args));
        return Assert.IsAssignableFrom<IEnumerable<AIContent>>(result).ToArray();
    }

    private static async Task<string> InvokeRawAsync(
        AIFunction tool, params (string Key, object? Value)[] args)
    {
        var result = await tool.InvokeAsync(Arguments(args));
        return result?.ToString() ?? string.Empty;
    }

    private sealed class FakeReader : IMailReader
    {
        public bool CanRender { get; init; } = true;

        public string RenderUnavailableReason => CanRender ? string.Empty : "渲染器未装";

        public IReadOnlyList<MailBrief> Hits { get; init; } = [];

        public MailRenderOutcome Render { get; init; } = MailRenderOutcome.Failed("没有预置渲染结果");

        public MailRawOutcome Raw { get; init; } = MailRawOutcome.Failed("没有预置原文");

        public List<Call> Calls { get; } = [];

        public (bool Segments, long MailId) LastSegments { get; private set; }

        public int LastMaxChars { get; private set; } = -1;

        public Task<IReadOnlyList<MailBrief>> LocateAsync(string reference, CancellationToken ct = default) =>
            Task.FromResult(Hits);

        public Task<MailRenderOutcome> RenderAsync(long mailId, bool segments, CancellationToken ct = default)
        {
            Calls.Add(new Call("render", mailId.ToString(), 0, segments));
            LastSegments = (segments, mailId);
            return Task.FromResult(Render);
        }

        public Task<MailRawOutcome> ReadRawAsync(long mailId, int maxChars, CancellationToken ct = default)
        {
            Calls.Add(new Call("raw", mailId.ToString(), maxChars, false));
            LastMaxChars = maxChars;
            return Task.FromResult(Raw);
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

        public Task AppendAuditAsync(string action, string actor, string? target, CancellationToken ct = default)
        {
            Audits.Add(new AuditEntry(Audits.Count + 1, action, actor, target, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditEntry>>(Audits);
    }

    /// <summary>只回答两次的假模型：第一次要求调工具，第二次把收到的消息记下来。</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private int _calls;

        public List<ChatMessage> SecondCallMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _calls++;

            if (_calls == 1)
            {
                var callContent = new FunctionCallContent(
                    "call-1",
                    MailTools.RenderToolName,
                    new Dictionary<string, object?> { ["mail"] = "11" });

                return Task.FromResult(new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, [callContent])));
            }

            SecondCallMessages.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "看到按钮了")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("本测试只跑非流式路径。");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
