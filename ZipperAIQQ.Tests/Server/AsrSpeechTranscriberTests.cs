using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Channel.Audio;
using ZipperAIQQ.Server.Ai;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// B00001：外部 ASR 转写（取音频 → 必要时解 SILK → B2 的 AsrAsync）。
/// </summary>
/// <remarks>
/// 全部用假实现：不打真实 DeepSeek，也不要求机器上装 Node / silk-wasm
/// （编码规范 §13：外部依赖用接口 + 假实现）。
/// </remarks>
public sealed class AsrSpeechTranscriberTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    /// <summary>最小合法 WAV 头（44 字节头 + 若干 PCM）。</summary>
    private static byte[] WavBytes(int pcmBytes = 8)
    {
        var bytes = new byte[44 + pcmBytes];
        bytes[0] = (byte)'R'; bytes[1] = (byte)'I'; bytes[2] = (byte)'F'; bytes[3] = (byte)'F';
        bytes[8] = (byte)'W'; bytes[9] = (byte)'A'; bytes[10] = (byte)'V'; bytes[11] = (byte)'E';
        return bytes;
    }

    private static byte[] SilkBytes() =>
        [0x02, 0x23, 0x21, 0x53, 0x49, 0x4C, 0x4B, 0x5F, 0x56, 0x33];   // 02 #!SILK_V3

    private sealed class FakeMedia(AsrResult? result, Exception? failure = null) : IDeepSeekMediaClient
    {
        public byte[]? LastAudio { get; private set; }

        public int Calls { get; private set; }

        public bool OcrAvailable => false;

        public bool TtsAvailable => false;

        public bool AsrAvailable { get; init; } = true;

        public Task<OcrResult> OcrAsync(byte[] file, string fileName, OcrPurpose purpose, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TtsResult> TtsAsync(string text, string? voiceId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AsrResult> AsrAsync(byte[] audio, string fileName, CancellationToken ct = default)
        {
            Calls++;
            LastAudio = audio;

            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(result ?? new AsrResult(string.Empty, 0, []));
        }
    }

    private sealed class FakeSource(params AudioBytes?[] responses) : IAudioSource
    {
        private int _index;

        public int Calls { get; private set; }

        public string? LastFileId { get; private set; }

        public Task<AudioBytes?> GetAudioAsync(string? fileId, string? filePath, CancellationToken ct = default)
        {
            Calls++;
            LastFileId = fileId;

            if (responses.Length == 0)
            {
                return Task.FromResult<AudioBytes?>(null);
            }

            var value = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(value);
        }
    }

    private sealed class FakeTranscoder(byte[]? wav) : IAudioTranscoder
    {
        public int Calls { get; private set; }

        public Task<byte[]?> ToWavAsync(byte[] source, string? format, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(wav);
        }
    }

    private sealed class FakeDecoder(byte[]? wav) : ISilkDecoder
    {
        public int Calls { get; private set; }

        public Task<byte[]?> ToWavAsync(byte[] silk, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(wav);
        }
    }

    /// <summary>假的 HTTP：默认 404（走兜底），需要时给一段"QQ 下载地址"的内容。</summary>
    private sealed class StubHttpFactory(byte[]? downloadBody = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(downloadBody), disposeHandler: true);
    }

    private sealed class StubHandler(byte[]? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (body is null)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };

            return Task.FromResult(response);
        }
    }

    /// <summary>默认把重试间隔压到 0：测的是"会不会重试"，不是"等多久"。</summary>
    private static AsrSpeechTranscriber Create(
        IDeepSeekMediaClient media,
        IAudioSource source,
        ISilkDecoder? decoder = null,
        IAudioTranscoder? transcoder = null,
        AiOptions? options = null,
        SpeechOptions? speech = null,
        IHttpClientFactory? http = null) =>
        new(options ?? new AiOptions(),
            media,
            source,
            transcoder ?? new FakeTranscoder(null),
            decoder ?? new FakeDecoder(WavBytes()),
            speech ?? new SpeechOptions { AudioFetchRetryDelayMs = 0 },
            http ?? new StubHttpFactory(),
            Logger);

    private static AudioClip Clip(string? url = null) => new("a.amr", url, MessageId: "m1");

    [Fact]
    public async Task Wav_audio_goes_straight_to_asr_without_decoding()
    {
        var media = new FakeMedia(new AsrResult("你发的语音说的是这个", 2300, []));
        var decoder = new FakeDecoder(WavBytes());

        var text = await Create(media, new FakeSource(new AudioBytes(WavBytes(), "wav")), decoder)
            .TranscribeAsync(Clip());

        Assert.Equal("你发的语音说的是这个", text);
        Assert.Equal(0, decoder.Calls);          // 已经是 WAV，不解
        Assert.Equal(1, media.Calls);
    }

    [Fact]
    public async Task Silk_audio_is_decoded_before_asr()
    {
        var decoded = WavBytes(1024);
        var media = new FakeMedia(new AsrResult("silk 解出来的话", 1740, []));
        var decoder = new FakeDecoder(decoded);

        var text = await Create(media, new FakeSource(new AudioBytes(SilkBytes(), "amr")), decoder)
            .TranscribeAsync(Clip());

        Assert.Equal("silk 解出来的话", text);
        Assert.Equal(1, decoder.Calls);
        Assert.Equal(decoded, media.LastAudio);   // 交给 ASR 的是解码后的 WAV
    }

    [Fact]
    public async Task Decode_failure_returns_null_and_does_not_retry()
    {
        var source = new FakeSource(new AudioBytes(SilkBytes(), "amr"));
        var media = new FakeMedia(new AsrResult("不该被调用", 0, []));

        var text = await Create(media, source, new FakeDecoder(null)).TranscribeAsync(Clip());

        Assert.Null(text);
        Assert.Equal(0, media.Calls);
        Assert.Equal(1, source.Calls);           // 同样的字节再解一次还是解不开
    }

    [Fact]
    public async Task Missing_audio_retries_then_gives_up()
    {
        var source = new FakeSource();   // 永远 null
        var media = new FakeMedia(new AsrResult("x", 0, []));

        var text = await Create(media, source, speech: new SpeechOptions
        {
            AudioFetchAttempts = 3,
            AudioFetchRetryDelayMs = 0,
        }).TranscribeAsync(Clip());

        Assert.Null(text);
        Assert.Equal(3, source.Calls);
        Assert.Equal(0, media.Calls);
    }

    [Fact]
    public async Task Missing_audio_then_success_on_retry()
    {
        var source = new FakeSource(null, new AudioBytes(WavBytes(), "wav"));
        var media = new FakeMedia(new AsrResult("重试之后拿到了", 1200, []));

        var text = await Create(media, source).TranscribeAsync(Clip());

        Assert.Equal("重试之后拿到了", text);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Media_exception_is_swallowed_as_null()
    {
        var media = new FakeMedia(
            null, new DeepSeekMediaException(DeepSeekMediaErrorKind.Unavailable, "没配 key"));

        var text = await Create(media, new FakeSource(new AudioBytes(WavBytes(), "wav")))
            .TranscribeAsync(Clip());

        Assert.Null(text);   // 转写失败不能让整条消息丢失
    }

    [Fact]
    public async Task Empty_asr_text_returns_null()
    {
        var media = new FakeMedia(new AsrResult("   ", 1000, []));

        var text = await Create(media, new FakeSource(new AudioBytes(WavBytes(), "wav")))
            .TranscribeAsync(Clip());

        Assert.Null(text);
    }

    [Fact]
    public async Task Asr_disabled_reports_unavailable_and_skips_everything()
    {
        var media = new FakeMedia(new AsrResult("x", 0, []));
        var source = new FakeSource(new AudioBytes(WavBytes(), "wav"));

        var transcriber = Create(media, source, options: new AiOptions { AsrEnabled = false });

        Assert.False(transcriber.IsAvailable);
        Assert.Null(await transcriber.TranscribeAsync(Clip()));
        Assert.Equal(0, media.Calls);
        Assert.Null(source.LastFileId);          // 连取音频都不该发生
    }

    [Fact]
    public void Client_unavailable_reports_unavailable()
    {
        var transcriber = Create(
            new FakeMedia(new AsrResult("x", 0, [])) { AsrAvailable = false },
            new FakeSource(new AudioBytes(WavBytes(), "wav")));

        Assert.False(transcriber.IsAvailable);
    }

    [Fact]
    public async Task Oversized_wav_is_rejected_before_asr()
    {
        var media = new FakeMedia(new AsrResult("x", 0, []));
        var big = WavBytes(1024);

        var text = await Create(media, new FakeSource(new AudioBytes(big, "wav")),
                options: new AiOptions { MaxUploadBytes = 100 })
            .TranscribeAsync(Clip());

        Assert.Null(text);
        Assert.Equal(0, media.Calls);
    }

    [Fact]
    public async Task Amr_audio_is_transcoded_by_ffmpeg_before_asr()
    {
        // URL 下载到的是 AMR（实测 1616 字节），ffmpeg 转 WAV 后再喂 ASR
        var amr = new byte[] { 0x23, 0x21, 0x41, 0x4D, 0x52, 0x0A };      // #!AMR
        var wav = WavBytes(256);
        var media = new FakeMedia(new AsrResult("amr 转出来的话", 2300, []));
        var transcoder = new FakeTranscoder(wav);
        var decoder = new FakeDecoder(WavBytes());   // 不该被调到

        var text = await Create(media, new FakeSource(new AudioBytes(amr, "amr")), decoder, transcoder)
            .TranscribeAsync(Clip());

        Assert.Equal("amr 转出来的话", text);
        Assert.Equal(1, transcoder.Calls);
        Assert.Equal(0, decoder.Calls);              // ffmpeg 成了就不必再试 silk
        Assert.Equal(wav, media.LastAudio);
    }

    [Fact]
    public async Task Silk_falls_back_to_silk_decoder_when_ffmpeg_cannot_handle_it()
    {
        // ffmpeg 解不了 SILK（Invalid frame size）→ silk-wasm 兜底
        var decoded = WavBytes(512);
        var media = new FakeMedia(new AsrResult("silk 兜底的话", 1740, []));
        var transcoder = new FakeTranscoder(null);

        var text = await Create(
                media, new FakeSource(new AudioBytes(SilkBytes(), "silk")),
                new FakeDecoder(decoded), transcoder)
            .TranscribeAsync(Clip());

        Assert.Equal("silk 兜底的话", text);
        Assert.Equal(1, transcoder.Calls);
        Assert.Equal(decoded, media.LastAudio);
    }

    // ══════════ 主线：走网络（record.data.url），不读本地文件 ══════════

    [Fact]
    public async Task Downloads_from_url_and_transcodes_silk_before_asr()
    {
        var silk = SilkBytes();
        var decoded = WavBytes(512);
        var media = new FakeMedia(new AsrResult("网络下载后转出来的话", 1740, []));
        var decoder = new FakeDecoder(decoded);

        var text = await Create(
                media,
                new FakeSource(),                       // 兜底不参与
                decoder,
                http: new StubHttpFactory(silk))
            .TranscribeAsync(Clip("https://multimedia.example/voice?format=amr"));

        Assert.Equal("网络下载后转出来的话", text);
        Assert.Equal(1, decoder.Calls);
        Assert.Equal(decoded, media.LastAudio);
    }

    [Fact]
    public async Task Falls_back_to_napcat_record_when_download_fails()
    {
        // 下载 404 时退回 NapCat 的 get_record（兜底），仍能转出来
        var media = new FakeMedia(new AsrResult("兜底拿到的", 900, []));

        var text = await Create(media, new FakeSource(new AudioBytes(WavBytes(), "wav")))
            .TranscribeAsync(Clip("https://multimedia.example/voice?format=amr"));

        Assert.Equal("兜底拿到的", text);
    }
}



