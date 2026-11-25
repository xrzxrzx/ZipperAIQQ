using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// 视频工具（<c>douyin_video</c> / <c>douyin_transcript</c>）：可见性、取点规划、如实回填、MEAI 透传。
/// </summary>
/// <remarks>
/// 用**真** <see cref="PermissionGate"/> + 真 <c>ToolCatalog</c> + 假 <see cref="IVideoReader"/>：
/// 容易写错的是「默认角色够不够」「底座没连上时工具在不在」「等分点算没算对」，
/// 这三条都得走真门禁、真目录表与真取点逻辑，自己再写一份口径等于测了个寂寞。
/// </remarks>
public sealed class VideoToolsTests
{
    // ───────────── 可见性 / 门禁 ─────────────

    [Fact]
    public void Ordinary_member_sees_no_video_tools()
    {
        Assert.Empty(CreateTools(Role.Normal).All);
    }

    [Fact]
    public void Admin_sees_both_tools_by_default()
    {
        var tools = CreateTools(Role.Admin);

        Assert.Equal(2, tools.All.Count);
        Assert.Contains(tools.All, tool => tool.Name == VideoTools.VideoToolName);
        Assert.Contains(tools.All, tool => tool.Name == VideoTools.TranscriptToolName);
    }

    [Fact]
    public void Reader_unavailable_hides_both_tools()
    {
        // 浏览器底座没连上：一个都不出现（不是"出现后每次都报未连接"）
        var tools = CreateTools(Role.Owner, reader: new FakeReader { CanRead = false });

        Assert.Empty(tools.All);
    }

    [Fact]
    public void Missing_asr_hides_only_transcript()
    {
        // 抽帧只要底座；转写还要 ASR。缺 ASR 时只剩看画面那一个
        var tools = CreateTools(Role.Owner, reader: new FakeReader { CanTranscribe = false });

        Assert.Single(tools.All);
        Assert.Equal(VideoTools.VideoToolName, tools.All[0].Name);
    }

    [Fact]
    public void Catalog_marks_video_tools_douyin_only()
    {
        Assert.Equal(ToolCapability.MediaVideo, ToolCatalog.CapabilityOf(VideoTools.VideoToolName));
        Assert.Equal(ToolCapability.MediaVideo, ToolCatalog.CapabilityOf(VideoTools.TranscriptToolName));

        // 只在抖音会话出现：QQ 群里给模型一个抖音工具，只会换来「查不到」的幻觉
        Assert.True(ToolCatalog.IsAvailableOn(VideoTools.VideoToolName, "douyin"));
        Assert.False(ToolCatalog.IsAvailableOn(VideoTools.VideoToolName, "qq"));
    }

    [Fact]
    public async Task Tool_rechecks_gate_even_when_schema_was_visible()
    {
        // 可见性与执行是两道闸：工具对象比当前角色旧时，执行必须被拒
        var reader = new FakeReader();
        var tool = VideoTools.Create(
                reader,
                new ToolCallContext { Chat = DouyinChat, Sender = Sender, Role = Role.Admin },
                new VisibleButDenyGate(),
                Logger)
            .OfType<AIFunction>()
            .Single(t => t.Name == VideoTools.VideoToolName);

        var result = await tool.InvokeAsync(Arguments(("reference", "7706296015465071715")));

        var contents = Assert.IsAssignableFrom<IEnumerable<AIContent>>(result);
        Assert.Contains(contents, c => c is TextContent text && text.Text.Contains("权限", StringComparison.Ordinal));
        Assert.Empty(reader.FrameCalls);
    }

    // ───────────── 取点规划（纯逻辑） ─────────────

    [Fact]
    public void First_mode_gives_only_zero()
    {
        Assert.True(VideoTools.TryParsePlan("first", 6, 0, 0, out var plan, out _));
        Assert.Equal([0L], VideoTools.ResolveTimes(plan, 28_000, out _));
    }

    [Fact]
    public void Even_mode_splits_whole_duration()
    {
        Assert.True(VideoTools.TryParsePlan("even", 4, 0, 0, out var plan, out _));
        // 28 s / 4 → 0, 7, 14, 21
        Assert.Equal([0L, 7_000L, 14_000L, 21_000L], VideoTools.ResolveTimes(plan, 28_000, out _));
    }

    [Fact]
    public void Even_mode_defaults_to_six_and_caps_at_sixteen()
    {
        Assert.True(VideoTools.TryParsePlan(null, 0, 0, 0, out var plan, out _));
        Assert.Equal(VideoTools.DefaultFrameCount, plan.Count);

        Assert.True(VideoTools.TryParsePlan("even", 999, 0, 0, out var capped, out _));
        Assert.Equal(VideoTools.HardMaxFrameCount, capped.Count);
    }

    [Fact]
    public void Even_mode_without_duration_says_why()
    {
        Assert.True(VideoTools.TryParsePlan("even", 3, 0, 0, out var plan, out _));

        var times = VideoTools.ResolveTimes(plan, 0, out var error);

        Assert.Null(times);
        Assert.Contains("没法等分", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_mode_includes_both_ends()
    {
        Assert.True(VideoTools.TryParsePlan("range", 3, 10, 20, out var plan, out _));

        var times = VideoTools.ResolveTimes(plan, 60_000, out _);

        Assert.Equal([10_000L, 15_000L, 20_000L], times);
    }

    [Fact]
    public void Range_mode_single_frame_lands_in_the_middle()
    {
        Assert.True(VideoTools.TryParsePlan("range", 1, 10, 20, out var plan, out _));

        Assert.Equal([15_000L], VideoTools.ResolveTimes(plan, 60_000, out _));
    }

    [Fact]
    public void Range_mode_with_open_end_uses_video_duration()
    {
        Assert.True(VideoTools.TryParsePlan("range", 2, 5, 0, out var plan, out _));

        Assert.Equal([5_000L, 28_000L], VideoTools.ResolveTimes(plan, 28_000, out _));
    }

    [Fact]
    public void Range_mode_open_end_without_duration_says_why()
    {
        Assert.True(VideoTools.TryParsePlan("range", 2, 5, 0, out var plan, out _));

        Assert.Null(VideoTools.ResolveTimes(plan, 0, out var error));
        Assert.Contains("给出 end", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_mode_clamps_end_beyond_duration()
    {
        Assert.True(VideoTools.TryParsePlan("range", 2, 20, 120, out var plan, out _));

        // 视频只有 28 s：区间被夹到 20–28
        Assert.Equal([20_000L, 28_000L], VideoTools.ResolveTimes(plan, 28_000, out _));
    }

    [Fact]
    public void Range_mode_rejects_empty_or_inverted_interval()
    {
        Assert.False(VideoTools.TryParsePlan("range", 3, 0, 0, out _, out var noRange));
        Assert.Contains("需要 start / end", noRange, StringComparison.Ordinal);

        Assert.False(VideoTools.TryParsePlan("range", 3, 20, 10, out _, out var inverted));
        Assert.Contains("end 必须大于 start", inverted, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_mode_is_reported_not_silently_ignored()
    {
        Assert.False(VideoTools.TryParsePlan("slideshow", 3, 0, 0, out _, out var error));
        Assert.Contains("first / even / range", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_past_duration_errors()
    {
        Assert.True(VideoTools.TryParsePlan("range", 2, 40, 50, out var plan, out _));

        Assert.Null(VideoTools.ResolveTimes(plan, 28_000, out var error));
        Assert.Contains("超过视频时长", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(9_000, "00:09")]
    [InlineData(62_000, "01:02")]
    [InlineData(3_723_000, "1:02:03")]
    public void FormatTime_reads_like_a_timestamp(long milliseconds, string expected)
    {
        Assert.Equal(expected, VideoTools.FormatTime(milliseconds));
    }

    // ───────────── 工具行为 ─────────────

    [Fact]
    public async Task Video_returns_caption_plus_one_image_per_frame()
    {
        var reader = new FakeReader
        {
            Brief = new VideoBrief("7706296015465071715", "戒网瘾 测试", 28_000),
            Frames = new VideoFramesOutcome(
                [
                    new VideoFrameImage(0, 0, [1, 2, 3], "image/jpeg"),
                    new VideoFrameImage(1, 14_000, [4, 5, 6], "image/jpeg"),
                ],
                "视频时长 00:28",
                null),
        };

        var tool = CreateTools(Role.Owner, reader).Video!;
        var contents = await InvokeVideoAsync(tool, ("reference", "[视频 id=7706296015465071715]"), ("count", 2));

        Assert.IsType<TextContent>(contents[0]);
        Assert.Equal(3, contents.Length);
        Assert.All(contents.Skip(1), c => Assert.IsType<DataContent>(c));
        Assert.Equal(2, reader.FrameCalls.Single().Count);
    }

    [Fact]
    public async Task Video_uses_the_parsed_id_not_the_raw_reference()
    {
        var reader = new FakeReader { Brief = new VideoBrief("7706296015465071715", "", 28_000) };

        await InvokeVideoAsync(
            CreateTools(Role.Owner, reader).Video!,
            ("reference", "https://www.douyin.com/video/7706296015465071715"),
            ("mode", "first"));

        Assert.Equal("7706296015465071715", reader.LastLocated);
        Assert.Equal([0L], reader.FrameCalls.Single());
    }

    [Fact]
    public async Task Video_reports_bad_reference_instead_of_crashing()
    {
        var reader = new FakeReader();
        var contents = await InvokeVideoAsync(CreateTools(Role.Owner, reader).Video!, ("reference", "这条视频"));

        var text = Assert.IsType<TextContent>(Assert.Single(contents));
        Assert.Contains("看不出要处理哪条视频", text.Text, StringComparison.Ordinal);
        Assert.Empty(reader.FrameCalls);
    }

    [Fact]
    public async Task Video_reports_frame_failure_verbatim()
    {
        var reader = new FakeReader
        {
            Brief = new VideoBrief("7706296015465071715", "", 28_000),
            Frames = VideoFramesOutcome.Failed("视频本体 80 MiB 超过上限 64 MiB"),
        };

        var contents = await InvokeVideoAsync(CreateTools(Role.Owner, reader).Video!, ("reference", "7706296015465071715"));

        var text = Assert.IsType<TextContent>(Assert.Single(contents));
        Assert.Contains("超过上限", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcript_formats_timestamped_lines()
    {
        var reader = new FakeReader
        {
            Transcript = new VideoTranscriptOutcome(
                [
                    new VideoTranscriptLine(0, 3_200, "第一句"),
                    new VideoTranscriptLine(3_200, 9_800, "第二句"),
                ],
                "第一句\n第二句",
                28_000,
                "区间 00:00–00:28",
                null),
        };

        var text = (await CreateTools(Role.Owner, reader).Transcript!.InvokeAsync(
            Arguments(("reference", "7706296015465071715"))))?.ToString();

        Assert.NotNull(text);
        Assert.Contains("[00:00–00:03] 第一句", text, StringComparison.Ordinal);
        Assert.Contains("[00:03–00:09] 第二句", text, StringComparison.Ordinal);
        Assert.Contains("ASR 原文", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcript_passes_seconds_as_milliseconds()
    {
        var reader = new FakeReader();

        await CreateTools(Role.Owner, reader).Transcript!.InvokeAsync(
            Arguments(("reference", "7706296015465071715"), ("start", 5.0), ("end", 12.5)));

        Assert.Equal((5_000L, 12_500L), reader.LastRange);
    }

    [Fact]
    public async Task Transcript_rejects_inverted_range_without_calling_reader()
    {
        var reader = new FakeReader();

        var result = await CreateTools(Role.Owner, reader).Transcript!.InvokeAsync(
            Arguments(("reference", "7706296015465071715"), ("start", 20.0), ("end", 10.0)));

        Assert.Contains("时间区间不对", result?.ToString(), StringComparison.Ordinal);
        Assert.Null(reader.LastRange);
    }

    // ───────────── MEAI 透传 ─────────────

    [Fact]
    public async Task Frames_survive_the_tool_loop_as_data_content()
    {
        // 与 mail_render 同一条关键契约：返回 AIContent[] 时必须还是**内容对象**，
        // 被序列化成 JSON 字符串的话模型只会看到一堆 base64（等于白抽帧）。
        var reader = new FakeReader
        {
            Brief = new VideoBrief("7706296015465071715", "", 28_000),
            Frames = new VideoFramesOutcome([new VideoFrameImage(0, 0, [7, 7, 7], "image/jpeg")], "", null),
        };

        var tool = CreateTools(Role.Owner, reader).Video!;
        var chat = new ScriptedChatClient();
        var client = new FunctionInvokingChatClient(chat);

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "看看这条视频")],
            new ChatOptions { Tools = [tool] });

        var functionResult = chat.SecondCallMessages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single();

        var contents = Assert.IsAssignableFrom<IEnumerable<AIContent>>(functionResult.Result).ToArray();
        Assert.Contains(contents, content => content is DataContent data && data.MediaType == "image/jpeg");
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

    private static (AIFunction? Video, AIFunction? Transcript, IReadOnlyList<AITool> All) CreateTools(
        Role role, IVideoReader? reader = null)
    {
        var all = VideoTools.Create(
            TestReader(reader),
            new ToolCallContext { Chat = DouyinChat, Sender = Sender, Role = role },
            new PermissionGate(),
            Logger).ToList();

        return (
            all.OfType<AIFunction>().FirstOrDefault(tool => tool.Name == VideoTools.VideoToolName),
            all.OfType<AIFunction>().FirstOrDefault(tool => tool.Name == VideoTools.TranscriptToolName),
            all);
    }

    /// <summary>没传就给一个「都可用」的假实现（可见性用例只需要它）。</summary>
    private static IVideoReader TestReader(IVideoReader? reader) => reader ?? new FakeReader();

    private static AIFunctionArguments Arguments(params (string Key, object? Value)[] args)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in args)
        {
            arguments[key] = value;
        }

        return arguments;
    }

    private static async Task<AIContent[]> InvokeVideoAsync(
        AIFunction tool, params (string Key, object? Value)[] args)
    {
        var result = await tool.InvokeAsync(Arguments(args));
        return Assert.IsAssignableFrom<IEnumerable<AIContent>>(result).ToArray();
    }

    private sealed class FakeReader : IVideoReader
    {
        public bool CanRead { get; init; } = true;

        public bool CanTranscribe { get; init; } = true;

        public string UnavailableReason => CanRead ? string.Empty : "抖音未连接";

        public VideoBrief Brief { get; init; } = new("7706296015465071715", "", 28_000);

        public VideoFramesOutcome Frames { get; init; } = VideoFramesOutcome.Failed("没有预置帧");

        public VideoTranscriptOutcome Transcript { get; init; } = VideoTranscriptOutcome.Failed("没有预置转写");

        public string? LastLocated { get; private set; }

        public List<List<long>> FrameCalls { get; } = [];

        public (long From, long To)? LastRange { get; private set; }

        public Task<VideoBrief> LocateAsync(string itemId, CancellationToken ct = default)
        {
            LastLocated = itemId;
            return Task.FromResult(Brief);
        }

        public Task<VideoFramesOutcome> FramesAsync(
            string itemId, IReadOnlyList<long> timesMs, CancellationToken ct = default)
        {
            FrameCalls.Add([.. timesMs]);
            return Task.FromResult(Frames);
        }

        public Task<VideoTranscriptOutcome> TranscribeAsync(
            string itemId, long startMs, long endMs, CancellationToken ct = default)
        {
            LastRange = (startMs, endMs);
            return Task.FromResult(Transcript);
        }
    }

    /// <summary>可见性放行、执行时拒绝的门禁（模拟「工具对象比当前角色旧」）。</summary>
    private sealed class VisibleButDenyGate : IPermissionGate
    {
        public PermissionDecision Check(ToolCapability capability, ToolCallContext context) =>
            PermissionDecision.Deny("权限不足：这个角色不能看视频。");

        public bool IsAllowed(ToolCapability capability, Role role) => true;
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
                    VideoTools.VideoToolName,
                    new Dictionary<string, object?> { ["reference"] = "7706296015465071715" });

                return Task.FromResult(new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, [callContent])));
            }

            SecondCallMessages.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "看到画面了")));
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
