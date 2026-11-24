using System.Buffers.Binary;
using System.Net;
using System.Text;
using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Server.Ai;
using ZipperAIQQ.Server.Ai.DeepSeekTools;

namespace ZipperAIQQ.Tests.Ai;

/// <summary>
/// 全家桶媒体三个客户端的契约与失败语义（OCR / TTS / ASR）。
/// </summary>
/// <remarks>
/// 全部走**假 HttpMessageHandler**：不发真请求、不花钱，但断言的是真报文
/// （路径、鉴权头、multipart 字段名、切片次数、落盘文件），而不是"内部状态"。
/// </remarks>
public sealed class DeepSeekMediaClientTests : IDisposable
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _ttsDir = Path.Combine(
        Path.GetTempPath(), $"zipper-tts-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_ttsDir))
            {
                Directory.Delete(_ttsDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不算失败
        }
    }

    // ───────────── OCR ─────────────

    [Fact]
    public async Task Ocr_parses_pages_usage_and_sends_the_purpose_field()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """
            {"page_count":2,"pages":[{"text":"第一页"},{"text":"第二页"}],
             "usage":{"input_tokens":245,"output_tokens":4}}
            """));
        var client = CreateOcr(handler);

        var result = await client.OcrAsync([1, 2, 3], "shot.png", OcrPurpose.RichLayoutRecognition);

        Assert.Equal(2, result.PageCount);
        Assert.Equal(2, result.Pages.Count);
        Assert.Equal("第一页", result.Pages[0].Text);
        Assert.Equal(2, result.Pages[1].Index);       // 平台没给 index → 按出现顺序补
        Assert.Equal(245, result.InputTokens);
        Assert.Equal(4, result.OutputTokens);

        Assert.Equal("/v1/tools/ocr", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer test-key", handler.LastRequest.Headers.Authorization!.ToString());
        Assert.Contains("name=purpose", handler.LastBody);
        Assert.Contains("rich_layout_recognition", handler.LastBody);
        Assert.Contains("name=file", handler.LastBody);
    }

    [Fact]
    public async Task Ocr_maps_http_failures_to_readable_kinds()
    {
        var cases = new (HttpStatusCode Status, DeepSeekMediaErrorKind Kind)[]
        {
            (HttpStatusCode.Unauthorized, DeepSeekMediaErrorKind.Auth),
            (HttpStatusCode.Forbidden, DeepSeekMediaErrorKind.Auth),
            (HttpStatusCode.RequestEntityTooLarge, DeepSeekMediaErrorKind.TooLarge),
            (HttpStatusCode.TooManyRequests, DeepSeekMediaErrorKind.RateLimited),
            (HttpStatusCode.BadRequest, DeepSeekMediaErrorKind.Usage),
            (HttpStatusCode.InternalServerError, DeepSeekMediaErrorKind.Upstream),
            (HttpStatusCode.GatewayTimeout, DeepSeekMediaErrorKind.Timeout),
        };

        foreach (var (status, kind) in cases)
        {
            var client = CreateOcr(new FakeHandler(_ => Json(status, "{}")));

            var ex = await Assert.ThrowsAsync<DeepSeekMediaException>(
                () => client.OcrAsync([1], "a.png", OcrPurpose.TextRecognition));

            Assert.Equal(kind, ex.Kind);
            Assert.Equal((int)status, ex.StatusCode);
        }
    }

    [Fact]
    public async Task Ocr_rejects_oversize_files_before_calling_the_api()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var client = CreateOcr(handler, maxUploadBytes: 2);

        var ex = await Assert.ThrowsAsync<DeepSeekMediaException>(
            () => client.OcrAsync([1, 2, 3], "big.png", OcrPurpose.TextRecognition));

        Assert.Equal(DeepSeekMediaErrorKind.TooLarge, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Ocr_unparsable_body_is_reported_as_parse_error_not_empty_result()
    {
        var client = CreateOcr(new FakeHandler(_ => Json(HttpStatusCode.OK, "<html>gateway</html>")));

        var ex = await Assert.ThrowsAsync<DeepSeekMediaException>(
            () => client.OcrAsync([1], "a.png", OcrPurpose.TextRecognition));

        Assert.Equal(DeepSeekMediaErrorKind.Parse, ex.Kind);
    }

    [Fact]
    public async Task Ocr_without_key_is_unavailable_and_never_hits_the_network()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var client = new OcrClient(
            new HttpClient(handler), new FakeKeys(null), Options(), Logger);

        var ex = await Assert.ThrowsAsync<DeepSeekMediaException>(
            () => client.OcrAsync([1], "a.png", OcrPurpose.TextRecognition));

        Assert.Equal(DeepSeekMediaErrorKind.Unavailable, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    // ───────────── TTS ─────────────

    [Fact]
    public async Task Tts_writes_the_audio_to_disk_and_reports_characters()
    {
        var audio = Encoding.ASCII.GetBytes("RIFF....WAVEfmt ");
        var handler = new FakeHandler(_ => Bytes(HttpStatusCode.OK, audio, "audio/wav"));
        var client = CreateTts(handler);

        var result = await client.TtsAsync("你好喵", voiceId: null);

        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(audio.Length, result.Bytes);
        Assert.Equal(audio.Length, new FileInfo(result.FilePath).Length);
        Assert.Equal("wav", result.Format);
        Assert.Equal(3, result.Characters);
        Assert.Contains("\"voice_id\":\"stella\"", handler.LastBody);   // 没给 voiceId → 用配置默认
    }

    [Fact]
    public async Task Tts_sends_voice_and_format_in_the_json_body()
    {
        var handler = new FakeHandler(_ => Bytes(HttpStatusCode.OK, [1, 2, 3], "audio/wav"));

        await CreateTts(handler).TtsAsync("念这句", voiceId: "tide");

        Assert.Equal("/v1/tools/tts", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("\"voice_id\":\"tide\"", handler.LastBody);
        Assert.Contains("\"format\":\"wav\"", handler.LastBody);
        Assert.Contains("filter_markdown", handler.LastBody);
    }

    [Fact]
    public async Task Tts_rejects_text_over_the_platform_limit_locally()
    {
        var handler = new FakeHandler(_ => Bytes(HttpStatusCode.OK, [1], "audio/wav"));
        var client = CreateTts(handler);

        var ex = await Assert.ThrowsAsync<DeepSeekMediaException>(
            () => client.TtsAsync(new string('字', 10_001)));

        Assert.Equal(DeepSeekMediaErrorKind.Usage, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Tts_never_writes_a_json_error_body_as_audio()
    {
        // 平台偶发「2xx + JSON 错误体」：当成音频存盘会生成一个打不开的文件还报成功
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"error":"bad voice"}"""));
        var client = CreateTts(handler);

        var ex = await Assert.ThrowsAsync<DeepSeekMediaException>(() => client.TtsAsync("你好"));

        Assert.Equal(DeepSeekMediaErrorKind.Parse, ex.Kind);
        Assert.False(Directory.Exists(_ttsDir) && Directory.GetFiles(_ttsDir).Length > 0);
    }

    [Fact]
    public async Task Tts_rejects_html_and_mislabeled_bodies_instead_of_saving_them()
    {
        // 只拦 application/json 不够：网关常回 text/html 或干脆不带 Content-Type
        var html = Bytes(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<html>502</html>"), "text/html");
        var exHtml = await Assert.ThrowsAsync<DeepSeekMediaException>(
            () => CreateTts(new FakeHandler(_ => html)).TtsAsync("你好"));
        Assert.Equal(DeepSeekMediaErrorKind.Parse, exHtml.Kind);

        var noType = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{not audio}")),
        };
        var exNoType = await Assert.ThrowsAsync<DeepSeekMediaException>(
            () => CreateTts(new FakeHandler(_ => noType)).TtsAsync("你好"));
        Assert.Equal(DeepSeekMediaErrorKind.Parse, exNoType.Kind);

        Assert.True(!Directory.Exists(_ttsDir) || Directory.GetFiles(_ttsDir).Length == 0);
    }

    // ───────────── ASR ─────────────

    [Fact]
    public async Task Asr_splits_long_wav_into_bounded_parts()
    {
        // 300 s @ 8 kHz 单声道 = 4.8 MB，每段 110 s ≈ 1.76 MB（远低于平台 8 MiB）
        var wav = BuildWav(sampleRate: 8000, seconds: 300);
        var handler = new FakeHandler(_ =>
            Json(HttpStatusCode.OK, """{"text":"第N段","duration_ms":1000}"""));

        var client = CreateAsr(handler);
        var result = await client.AsrAsync(wav, "long.wav");

        Assert.Equal(3, handler.Calls);                       // 110 s 步长 109 s → 3 段
        Assert.Equal(3, result.Parts.Count);
        Assert.InRange(result.DurationMs, 299_900, 300_100);  // 用原始音频时长，不是分段累加
        Assert.Contains("第N段", result.Text);
        Assert.All(handler.PartSizes, size => Assert.True(size < 8 * 1024 * 1024));
    }

    [Fact]
    public async Task Asr_cuts_by_bytes_too_so_44100_stereo_stays_under_8_mib()
    {
        // 回归：只按时长切时，60 s 的 44.1 kHz 立体声就是 10.09 MiB —— 一段就超平台上限
        var wav = BuildWav(sampleRate: 44100, seconds: 60, channels: 2);
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"text":"口述","duration_ms":1000}"""));

        var result = await CreateAsr(handler).AsrAsync(wav, "phone.wav");

        Assert.True(handler.Calls >= 2, $"60 s 立体声必须切开，实际只发了 {handler.Calls} 次");
        Assert.All(handler.PartSizes, size => Assert.True(size < 8 * 1024 * 1024, $"分段 {size} 字节仍超 8 MiB"));
        Assert.All(result.Parts, p => Assert.True(p.EndMs - p.StartMs <= 110_000));
        Assert.InRange(result.DurationMs, 59_900, 60_100);
    }

    [Fact]
    public async Task Asr_reports_the_unsliceable_format_honestly()
    {
        // 一个 9 MiB 的 ogg：切不了（非 PCM），只能整段发 → 超 8 MiB 必须本地拒，
        // 而且要说清下一步（转 16 kHz 单声道 WAV），不能只说"太大"
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));

        var ex = await Assert.ThrowsAsync<DeepSeekMediaException>(
            () => CreateAsr(handler).AsrAsync(new byte[9 * 1024 * 1024], "voice.ogg"));

        Assert.Equal(DeepSeekMediaErrorKind.TooLarge, ex.Kind);
        Assert.Contains("16 kHz", ex.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Asr_sends_short_wav_whole()
    {
        var wav = BuildWav(sampleRate: 8000, seconds: 20);
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"text":"你好","duration_ms":20000}"""));

        var result = await CreateAsr(handler).AsrAsync(wav, "short.wav");

        Assert.Equal(1, handler.Calls);
        Assert.Single(result.Parts);
        Assert.Equal("你好", result.Text);
        Assert.Equal(20_000, result.DurationMs);
        Assert.Equal(0, result.Parts[0].StartMs);
        Assert.Equal(20_000, result.Parts[0].EndMs);
    }

    [Fact]
    public async Task Asr_refuses_an_oversize_non_wav_blob_without_calling_the_api()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));

        var ex = await Assert.ThrowsAsync<DeepSeekMediaException>(
            () => CreateAsr(handler).AsrAsync(new byte[9 * 1024 * 1024], "voice.ogg"));

        Assert.Equal(DeepSeekMediaErrorKind.TooLarge, ex.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Asr_fails_the_whole_call_when_a_later_part_has_no_text()
    {
        // 半截转写最容易被当成完整内容引用，所以任一段 parse 失败就整次失败。
        // 这里**故意让第 1 段成功、第 2 段缺 text** —— 那是最容易被后人"加个 try/catch 容错"
        // 悄悄改成"返回前半截"的位置。
        var wav = BuildWav(sampleRate: 8000, seconds: 300);
        var calls = 0;
        var handler = new FakeHandler(_ =>
        {
            calls++;
            return calls == 1
                ? Json(HttpStatusCode.OK, """{"text":"前半段","duration_ms":109000}""")
                : Json(HttpStatusCode.OK, """{"duration_ms":1000}""");
        });

        var ex = await Assert.ThrowsAsync<DeepSeekMediaException>(
            () => CreateAsr(handler).AsrAsync(wav, "long.wav"));

        Assert.Equal(DeepSeekMediaErrorKind.Parse, ex.Kind);
        Assert.Equal(2, handler.Calls);
    }

    // ───────────── 辅助 ─────────────

    private OcrClient CreateOcr(FakeHandler handler, int maxUploadBytes = 20 * 1024 * 1024) =>
        new(new HttpClient(handler), new FakeKeys("test-key"),
            Options() with { MaxUploadBytes = maxUploadBytes }, Logger);

    private TtsClient CreateTts(FakeHandler handler) =>
        new(new HttpClient(handler), new FakeKeys("test-key"), Options(), Logger);

    private AsrClient CreateAsr(FakeHandler handler) =>
        new(new HttpClient(handler), new FakeKeys("test-key"), Options(), Logger);

    private AiOptions Options() => new()
    {
        BaseUrl = "https://api.deepseek.test",
        TtsOutputDir = _ttsDir,
        TtsVoiceId = "stella",
        TtsFormat = "wav",
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Bytes(HttpStatusCode status, byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(status) { Content = content };
    }

    /// <summary>造一个 16-bit 静音 WAV。</summary>
    private static byte[] BuildWav(int sampleRate, int seconds, int channels = 1)
    {
        var blockAlign = channels * 2;
        var dataLength = sampleRate * seconds * blockAlign;
        var wav = new byte[44 + dataLength];
        var span = wav.AsSpan();

        Encoding.ASCII.GetBytes("RIFF", span[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVE", span.Slice(8, 4));
        Encoding.ASCII.GetBytes("fmt ", span.Slice(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), 16);
        Encoding.ASCII.GetBytes("data", span.Slice(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), dataLength);

        return wav;
    }

    private sealed class FakeKeys(string? key) : IDeepSeekKeyProvider
    {
        public bool HasKey => !string.IsNullOrEmpty(key);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetAsync(CancellationToken ct = default) => Task.FromResult(key);

        public DeepSeekKeyStatus GetStatus() =>
            new(HasKey, HasKey ? 1 : 0, HasKey ? "sk-****" : string.Empty);

        public Task<bool> SetKeyAsync(string plainKey, string actor, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public int Calls { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        /// <summary>每次请求的 Content-Length（验证分段体积用）。</summary>
        public List<long> PartSizes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;

            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                PartSizes.Add(bytes.Length);

                // multipart 里的二进制会被 UTF-8 解码成替换字符，但**字段名与 Content-Disposition 是 ASCII**，
                // 所以同一份解码结果足够断言 name=purpose / name=file 这类结构
                LastBody = Encoding.UTF8.GetString(bytes);
            }

            return _respond(request);
        }
    }
}
