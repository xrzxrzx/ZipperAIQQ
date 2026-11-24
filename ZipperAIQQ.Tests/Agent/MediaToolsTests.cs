using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// 媒体工具（<c>ocr_image</c> / <c>tts_speak</c>）：可见性、门禁、图片来源、如实回报。
/// </summary>
/// <remarks>
/// 用**真 PermissionGate**（默认配置）+ 假 media 客户端：可见性这条最容易写错的地方，
/// 恰恰是「默认角色够不够、开关关掉后还在不在」—— 这两条必须走真门禁与真目录表。
/// </remarks>
public sealed class MediaToolsTests : IDisposable
{
    private readonly string _workspaceRoot = Path.Combine(
        Path.GetTempPath(), $"zipper-media-tools-{Guid.NewGuid():N}");

    private readonly ToolCallContext _context = new()
    {
        Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "10001" },
        Sender = new SenderInfo { UserId = "10001", Nickname = "测试用户" },
        Role = Role.Normal,
    };

    public MediaToolsTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "qq", "p10001"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不算失败
        }
    }

    [Fact]
    public void Unavailable_client_yields_no_tools_at_all()
    {
        var tools = CreateTools(new FakeMedia { OcrAvailable = false, TtsAvailable = false });

        Assert.Empty(tools.All);
    }

    [Fact]
    public void Normal_role_sees_both_tools()
    {
        var tools = CreateTools(new FakeMedia());

        Assert.Equal(2, tools.All.Count);
        Assert.Contains(tools.All, t => t.Name == MediaTools.OcrToolName);
        Assert.Contains(tools.All, t => t.Name == MediaTools.TtsToolName);
    }

    [Fact]
    public void Disabled_tts_hides_only_that_tool()
    {
        var tools = CreateTools(new FakeMedia { TtsAvailable = false });

        Assert.Single(tools.All);
        Assert.Equal(MediaTools.OcrToolName, tools.All[0].Name);
    }

    [Fact]
    public void Catalog_declares_capabilities_for_both_tools()
    {
        Assert.Equal(ToolCapability.MediaRead, ToolCatalog.CapabilityOf(MediaTools.OcrToolName));
        Assert.Equal(ToolCapability.MediaSpeak, ToolCatalog.CapabilityOf(MediaTools.TtsToolName));
        // 无平台专属：在 QQ 与抖音会话里都应出现
        Assert.True(ToolCatalog.IsAvailableOn(MediaTools.OcrToolName, "qq"));
        Assert.True(ToolCatalog.IsAvailableOn(MediaTools.OcrToolName, "douyin"));
    }

    [Fact]
    public async Task Ocr_reads_a_workspace_file_and_returns_page_text()
    {
        await File.WriteAllBytesAsync(
            Path.Combine(_workspaceRoot, "qq", "p10001", "shot.png"), [1, 2, 3]);

        var media = new FakeMedia
        {
            OcrResult = new OcrResult(2, [new OcrPage(1, "第一页"), new OcrPage(2, "第二页")], 10, 2),
        };

        var text = await InvokeAsync(
            CreateTools(media, role: Role.Whitelist).Ocr!, ("source", "shot.png"));

        Assert.Contains("第一页", text);
        Assert.Contains("第二页", text);
        Assert.Contains("2 页", text);
        Assert.Equal("shot.png", media.LastFileName);
        Assert.Equal(OcrPurpose.TextRecognition, media.LastPurpose);
    }

    [Fact]
    public async Task Workspace_ocr_requires_the_same_role_as_file_read()
    {
        // 旁路回归：MediaRead 默认 Normal，而 ReadFile 默认只给白名单。
        // 不加这道闸，普通成员就能用 OCR 把「读不到的文件」读成文字。
        await File.WriteAllBytesAsync(
            Path.Combine(_workspaceRoot, "qq", "p10001", "secret.png"), [1]);

        var media = new FakeMedia();
        var text = await InvokeAsync(
            CreateTools(media, role: Role.Normal).Ocr!, ("source", "secret.png"));

        Assert.Contains("权限不足", text);
        Assert.Null(media.LastFileName);   // 根本没走到识别接口
    }

    [Fact]
    public async Task Message_image_still_works_for_normal_members()
    {
        // 他自己发的图属于他自己的内容：只走 MediaRead，不该被 ReadFile 卡住
        var segments = new MessageSegment[] { new ImageSegment("https://example.test/a.jpg", null) };
        var fetcher = new FakeFetcher(new FetchedImage([7], "image/jpeg"));

        var text = await InvokeAsync(
            CreateTools(new FakeMedia(), segments: segments, fetcher: fetcher, role: Role.Normal).Ocr!,
            ("source", "msg:1"));

        Assert.DoesNotContain("权限不足", text);
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task Ocr_ignores_image_segments_without_an_absolute_url()
    {
        // 与 ChatAgent 渲染上下文的谓词保持一致：只有绝对 URL 才会被当成图投喂给模型，
        // 所以「第 N 张」也只能数这些，否则模型数的和工具数的会错位。
        var segments = new MessageSegment[]
        {
            new ImageSegment("not-a-url", "file-x"),
            new ImageSegment("https://example.test/b.png", "file-y"),
        };

        var fetcher = new FakeFetcher(new FetchedImage([1], "image/png"));
        var media = new FakeMedia();

        await InvokeAsync(
            CreateTools(media, segments: segments, fetcher: fetcher).Ocr!, ("source", "msg:1"));

        Assert.Equal(1, fetcher.Calls);
        Assert.Equal("msg1.png", media.LastFileName);
    }

    [Fact]
    public async Task Ocr_can_target_the_current_message_image()
    {
        var media = new FakeMedia
        {
            OcrResult = new OcrResult(1, [new OcrPage(1, "图里的字")]),
        };

        var segments = new MessageSegment[]
        {
            new TextSegment("看这个"),
            new ImageSegment("https://example.test/a.jpg", "file-1"),
        };

        var fetcher = new FakeFetcher(new FetchedImage([9, 9, 9], "image/png"));
        var text = await InvokeAsync(
            CreateTools(media, segments: segments, fetcher: fetcher).Ocr!, ("source", "msg:1"));

        Assert.Contains("图里的字", text);
        Assert.Equal(1, fetcher.Calls);
        Assert.Equal("msg1.png", media.LastFileName);   // MIME 决定扩展名
    }

    [Fact]
    public async Task Ocr_reports_a_missing_message_image_instead_of_guessing()
    {
        var text = await InvokeAsync(
            CreateTools(new FakeMedia(), segments: [new TextSegment("没图")], fetcher: new FakeFetcher(null))
                .Ocr!,
            ("source", "msg:1"));

        Assert.Contains("没有图片", text);
    }

    [Fact]
    public async Task Workspace_image_larger_than_file_max_bytes_still_reaches_ocr()
    {
        // P0 回归：媒体工具曾经拿 FileMaxBytes（默认 256 KiB）当上传上限，
        // 一张 1080p 截图（1–3 MiB）就走不通。这里用 300 KiB 精确卡在旧上限之上。
        var bytes = new byte[300 * 1024];
        await File.WriteAllBytesAsync(Path.Combine(_workspaceRoot, "qq", "p10001", "photo.jpg"), bytes);

        var media = new FakeMedia();
        var text = await InvokeAsync(
            CreateTools(media, role: Role.Whitelist).Ocr!, ("source", "photo.jpg"));

        Assert.DoesNotContain("太大", text);
        Assert.Equal("photo.jpg", media.LastFileName);
    }

    [Fact]
    public async Task Workspace_image_over_the_media_limit_is_refused_with_that_number()
    {
        await File.WriteAllBytesAsync(
            Path.Combine(_workspaceRoot, "qq", "p10001", "huge.jpg"), new byte[2 * 1024 * 1024]);

        var media = new FakeMedia();
        var text = await InvokeAsync(
            CreateTools(media, role: Role.Whitelist, maxUploadBytes: 1024 * 1024).Ocr!,
            ("source", "huge.jpg"));

        Assert.Contains("1048576", text);   // 报的是媒体上限，不是 256 KiB 那个旧旋钮
        Assert.Null(media.LastFileName);
    }

    [Fact]
    public async Task Ocr_refuses_paths_outside_the_session_workspace()
    {
        var text = await InvokeAsync(
            CreateTools(new FakeMedia(), role: Role.Whitelist).Ocr!, ("source", "../../secret.png"));

        Assert.Contains("拒绝", text);
    }

    [Fact]
    public async Task Ocr_passes_media_failures_through_as_plain_words()
    {
        var media = new FakeMedia
        {
            OcrError = new DeepSeekMediaException(DeepSeekMediaErrorKind.TooLarge, "文件 30 MB"),
        };

        await File.WriteAllBytesAsync(
            Path.Combine(_workspaceRoot, "qq", "p10001", "big.png"), [1]);

        var text = await InvokeAsync(
            CreateTools(media, role: Role.Whitelist).Ocr!, ("source", "big.png"));

        Assert.Contains("文件太大", text);
        Assert.DoesNotContain("成功", text);
    }

    [Fact]
    public async Task Tts_says_the_file_stays_on_the_server()
    {
        var media = new FakeMedia
        {
            TtsResult = new TtsResult(@"D:\data\ai-tts\tts-1.wav", 1024, "wav", 5),
        };

        var text = await InvokeAsync(CreateTools(media).Tts!, ("text", "你好喵"));

        Assert.Contains("tts-1.wav", text);
        Assert.Contains("只落文件", text);
        Assert.Contains("不自动发语音", text);
    }

    [Fact]
    public async Task Tts_rejects_empty_text_without_calling_the_api()
    {
        var media = new FakeMedia();
        var text = await InvokeAsync(CreateTools(media).Tts!, ("text", "   "));

        Assert.Contains("请给出", text);
        Assert.Equal(0, media.TtsCalls);
    }

    [Fact]
    public async Task Ocr_and_tts_record_usage_by_billing_unit()
    {
        await File.WriteAllBytesAsync(
            Path.Combine(_workspaceRoot, "qq", "p10001", "shot.png"), [1, 2, 3]);

        var usage = new FakeUsageSink();
        var media = new FakeMedia
        {
            OcrResult = new OcrResult(3, [new OcrPage(1, "字")]),
            TtsResult = new TtsResult(@"D:\data\ai-tts\x.wav", 10, "wav", 42),
        };

        await InvokeAsync(
            CreateTools(media, role: Role.Whitelist, usage: usage).Ocr!, ("source", "shot.png"));
        await InvokeAsync(CreateTools(media, usage: usage).Tts!, ("text", "念这句"));

        Assert.Equal(2, usage.Records.Count);
        Assert.Equal(("ocr", "page", 3.0), usage.Records[0]);
        Assert.Equal(("tts", "char", 42.0), usage.Records[1]);
        // 会话号要带上，否则用量没法按会话筛
        Assert.All(usage.SessionIds, id => Assert.Equal("qq:p10001", id));
    }

    // ───────────── 辅助 ─────────────

    private (AIFunction? Ocr, AIFunction? Tts, IReadOnlyList<AITool> All) CreateTools(
        FakeMedia media,
        IReadOnlyList<MessageSegment>? segments = null,
        IImageFetcher? fetcher = null,
        Role role = Role.Normal,
        int maxUploadBytes = 20 * 1024 * 1024,
        IToolUsageSink? usage = null)
    {
        var context = new ToolCallContext
        {
            Chat = _context.Chat,
            Sender = _context.Sender,
            Role = role,
        };

        var all = MediaTools.Create(
            context,
            new PermissionGate(),
            new WorkspacePathResolver(_workspaceRoot),
            maxUploadBytes: maxUploadBytes,
            media,
            fetcher,
            segments ?? [],
            new LoggerConfiguration().CreateLogger(),
            usage);

        return (
            all.OfType<AIFunction>().FirstOrDefault(t => t.Name == MediaTools.OcrToolName),
            all.OfType<AIFunction>().FirstOrDefault(t => t.Name == MediaTools.TtsToolName),
            all);
    }

    private static async Task<string> InvokeAsync(
        AIFunction tool, params (string Key, object? Value)[] args)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in args)
        {
            arguments[key] = value;
        }

        var result = await tool.InvokeAsync(arguments);
        return result?.ToString() ?? string.Empty;
    }

    private sealed class FakeMedia : IDeepSeekMediaClient
    {
        public bool OcrAvailable { get; init; } = true;

        public bool TtsAvailable { get; init; } = true;

        public bool AsrAvailable => true;

        public OcrResult OcrResult { get; init; } = new(1, [new OcrPage(1, "x")]);

        public DeepSeekMediaException? OcrError { get; init; }

        public TtsResult TtsResult { get; init; } = new("/tmp/t.wav", 1, "wav", 1);

        public string? LastFileName { get; private set; }

        public OcrPurpose LastPurpose { get; private set; }

        public int TtsCalls { get; private set; }

        public Task<OcrResult> OcrAsync(
            byte[] file, string fileName, OcrPurpose purpose, CancellationToken ct = default)
        {
            LastFileName = fileName;
            LastPurpose = purpose;

            return OcrError is null
                ? Task.FromResult(OcrResult)
                : Task.FromException<OcrResult>(OcrError);
        }

        public Task<TtsResult> TtsAsync(string text, string? voiceId = null, CancellationToken ct = default)
        {
            TtsCalls++;
            return Task.FromResult(TtsResult);
        }

        public Task<AsrResult> AsrAsync(byte[] audio, string fileName, CancellationToken ct = default) =>
            Task.FromResult(new AsrResult("x", 0, [new AsrSegment(0, 0, "x")]));
    }

    private sealed class FakeFetcher(FetchedImage? image) : IImageFetcher
    {
        public int Calls { get; private set; }

        public Task<FetchedImage?> FetchAsync(string url, string? fileId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(image);
        }
    }

    private sealed class FakeUsageSink : IToolUsageSink
    {
        public List<(string Kind, string Unit, double Amount)> Records { get; } = [];

        public List<string?> SessionIds { get; } = [];

        public Task RecordAsync(
            string kind, string unit, double amount, string? sessionId, CancellationToken ct = default)
        {
            Records.Add((kind, unit, amount));
            SessionIds.Add(sessionId);
            return Task.CompletedTask;
        }
    }
}
