using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Channel.Audio;
using ZipperAIQQ.Server.Ai;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// B00001：外部 ASR 转写（取 WAV → B2 的 AsrAsync）。
/// </summary>
/// <remarks>
/// 全部用假实现：不打真实 DeepSeek，也不要求平台侧能转码
/// （编码规范 §13：外部依赖用接口 + 假实现）。
/// </remarks>
public sealed class AsrSpeechTranscriberTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

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

    private sealed class FakeSource(byte[]? wav) : IAudioSource
    {
        public string? LastFileId { get; private set; }

        public Task<byte[]?> GetWavAsync(string? fileId, string? filePath, CancellationToken ct = default)
        {
            LastFileId = fileId;
            return Task.FromResult(wav);
        }
    }

    private static AsrSpeechTranscriber Create(
        IDeepSeekMediaClient media, IAudioSource source, AiOptions? options = null) =>
        new(options ?? new AiOptions(), media, source, Logger);

    private static AudioClip Clip() => new("a.amr", "https://x/a.amr", MessageId: "m1");

    [Fact]
    public async Task Wav_bytes_are_passed_to_asr_and_text_returned()
    {
        var media = new FakeMedia(new AsrResult("你发的语音说的是这个", 2300, []));
        var source = new FakeSource([1, 2, 3, 4]);

        var text = await Create(media, source).TranscribeAsync(Clip());

        Assert.Equal("你发的语音说的是这个", text);
        Assert.Equal(1, media.Calls);
        Assert.Equal([1, 2, 3, 4], media.LastAudio);
        Assert.Equal("a.amr", source.LastFileId);
    }

    [Fact]
    public async Task Missing_audio_returns_null_without_calling_asr()
    {
        var media = new FakeMedia(new AsrResult("不该被调用", 0, []));

        var text = await Create(media, new FakeSource(null)).TranscribeAsync(Clip());

        Assert.Null(text);
        Assert.Equal(0, media.Calls);
    }

    [Fact]
    public async Task Empty_audio_returns_null()
    {
        var media = new FakeMedia(new AsrResult("不该被调用", 0, []));

        var text = await Create(media, new FakeSource([])).TranscribeAsync(Clip());

        Assert.Null(text);
        Assert.Equal(0, media.Calls);
    }

    [Fact]
    public async Task Media_exception_is_swallowed_as_null()
    {
        var media = new FakeMedia(
            null, new DeepSeekMediaException(DeepSeekMediaErrorKind.Unavailable, "没配 key"));

        var text = await Create(media, new FakeSource([1, 2, 3])).TranscribeAsync(Clip());

        Assert.Null(text);   // 转写失败不能让整条消息丢失
    }

    [Fact]
    public async Task Empty_asr_text_returns_null()
    {
        var media = new FakeMedia(new AsrResult("   ", 1000, []));

        var text = await Create(media, new FakeSource([1, 2, 3])).TranscribeAsync(Clip());

        Assert.Null(text);
    }

    [Fact]
    public async Task Asr_disabled_reports_unavailable_and_skips_everything()
    {
        var media = new FakeMedia(new AsrResult("x", 0, []));
        var source = new FakeSource([1, 2, 3]);

        var transcriber = Create(media, source, new AiOptions { AsrEnabled = false });

        Assert.False(transcriber.IsAvailable);
        Assert.Null(await transcriber.TranscribeAsync(Clip()));
        Assert.Equal(0, media.Calls);
        Assert.Null(source.LastFileId);   // 连取音频都不该发生
    }

    [Fact]
    public void Client_unavailable_reports_unavailable()
    {
        var transcriber = Create(
            new FakeMedia(new AsrResult("x", 0, [])) { AsrAvailable = false },
            new FakeSource([1, 2, 3]));

        Assert.False(transcriber.IsAvailable);
    }

    [Fact]
    public async Task Oversized_wav_is_rejected_before_asr()
    {
        var media = new FakeMedia(new AsrResult("x", 0, []));

        var text = await Create(media, new FakeSource(new byte[64]), new AiOptions { MaxUploadBytes = 16 })
            .TranscribeAsync(Clip());

        Assert.Null(text);
        Assert.Equal(0, media.Calls);
    }
}
