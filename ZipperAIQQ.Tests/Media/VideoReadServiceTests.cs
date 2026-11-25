using System.Text;
using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Server.Ai.DeepSeekTools;
using ZipperAIQQ.Server.Channels;
using ZipperAIQQ.Server.Media;

namespace ZipperAIQQ.Tests.Media;

/// <summary>
/// 视频理解服务：窗口循环 / 拼回整段 / 时间戳对齐 / 上限截断 / 失败如实回填。
/// </summary>
/// <remarks>
/// 用假 <see cref="IDouyinMediaSource"/>（按请求区间当场造一段 16 kHz 单声道 WAV）
/// 与假 <see cref="IDeepSeekMediaClient"/>（记录喂进来的 WAV 有多长、回一段带时间戳的文本）：
/// 这里要证明的是**服务端的编排**（窗口边界、偏移、上限、降级），不是浏览器或 ASR 本身。
/// </remarks>
public sealed class VideoReadServiceTests
{
    private const string ItemId = "7706296015465071715";

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task Transcribe_walks_the_whole_video_in_windows_and_stitches_one_wav()
    {
        var source = new FakeSource { DurationMs = 60_000 };
        var media = new FakeMedia();
        var service = CreateService(source, media);

        var outcome = await service.TranscribeAsync(ItemId, 0, 0);

        Assert.Null(outcome.Error);
        // 60 秒 / 20 秒窗口 = 3 次；服务端拼成**一整段**再发（一段一段发会把句子切成两半）
        Assert.Equal([(0L, 20_000L), (20_000L, 40_000L), (40_000L, 60_000L)], source.AudioCalls);
        Assert.Single(media.AsrCalls);
        Assert.Equal(60_000, media.LastWavDurationMs);
        Assert.Equal(60_000, outcome.DurationMs);
    }

    [Fact]
    public async Task Transcribe_offsets_timestamps_by_the_requested_start()
    {
        var source = new FakeSource { DurationMs = 60_000 };
        var media = new FakeMedia();
        var service = CreateService(source, media);

        var outcome = await service.TranscribeAsync(ItemId, 10_000, 40_000);

        Assert.Null(outcome.Error);
        Assert.Equal([(10_000L, 30_000L), (30_000L, 40_000L)], source.AudioCalls);
        Assert.Equal(30_000, media.LastWavDurationMs);

        // ASR 的时间戳是相对「喂给它的那段音频」的，必须再抬到视频里的绝对位置
        Assert.Equal(10_000, outcome.Lines[0].StartMs);
        Assert.Equal(13_200, outcome.Lines[0].EndMs);
        Assert.Equal(13_200, outcome.Lines[1].StartMs);
    }

    [Fact]
    public async Task Transcribe_clips_to_the_configured_max_audio_seconds()
    {
        var source = new FakeSource { DurationMs = 600_000 };
        var media = new FakeMedia();
        var service = CreateService(source, media, new DouyinVideoOptions { MaxAudioSeconds = 30 });

        var outcome = await service.TranscribeAsync(ItemId, 0, 0);

        Assert.Null(outcome.Error);
        // 10 分钟的视频只转前 30 秒（默认上限 180 秒在这里被配置压到 30）
        Assert.Equal([(0L, 20_000L), (20_000L, 30_000L)], source.AudioCalls);
        Assert.Equal(30_000, media.LastWavDurationMs);
        Assert.Contains("超过单次转写上限", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcribe_reports_no_voice_without_pretending_it_failed()
    {
        var source = new FakeSource { DurationMs = 20_000 };
        var media = new FakeMedia { Text = string.Empty, Parts = [] };
        var service = CreateService(source, media);

        var outcome = await service.TranscribeAsync(ItemId, 0, 0);

        Assert.Null(outcome.Error);
        Assert.Equal(string.Empty, outcome.Text);
        Assert.Contains("没有识别到人声", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcribe_fails_when_base_audio_is_not_pcm_wav()
    {
        var source = new FakeSource { DurationMs = 20_000, BrokenAudio = true };
        var media = new FakeMedia();
        var service = CreateService(source, media);

        var outcome = await service.TranscribeAsync(ItemId, 0, 0);

        Assert.NotNull(outcome.Error);
        Assert.Contains("PCM WAV", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(media.AsrCalls);
    }

    [Fact]
    public async Task Transcribe_does_not_touch_the_sidecar_when_asr_is_off()
    {
        var source = new FakeSource { DurationMs = 20_000 };
        var media = new FakeMedia { AsrAvailable = false };
        var service = CreateService(source, media);

        var outcome = await service.TranscribeAsync(ItemId, 0, 0);

        Assert.NotNull(outcome.Error);
        Assert.Contains("ASR", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(source.AudioCalls);
        Assert.Empty(media.AsrCalls);
    }

    [Fact]
    public async Task Transcribe_rejects_too_short_range()
    {
        var source = new FakeSource { DurationMs = 20_000 };
        var service = CreateService(source, new FakeMedia());

        var outcome = await service.TranscribeAsync(ItemId, 5_000, 5_400);

        Assert.NotNull(outcome.Error);
        Assert.Contains("太短", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(source.AudioCalls);
    }

    // ───────────── 抽帧 ─────────────

    [Fact]
    public async Task Frames_dedupes_sorts_and_caps_the_points()
    {
        var source = new FakeSource();
        var service = CreateService(source, new FakeMedia(), new DouyinVideoOptions { MaxFrames = 3 });

        var outcome = await service.FramesAsync(ItemId, [14_000, 0, 0, 7_000, 21_000, 28_000]);

        Assert.Null(outcome.Error);
        // 排序 + 去重 + 只留前 3 个
        Assert.Equal([0L, 7_000L, 14_000L], source.FrameCalls.Single());
        Assert.Contains("多余的没截", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frames_are_unavailable_when_the_sidecar_is_not_connected()
    {
        var source = new FakeSource { Ready = false };
        var service = CreateService(source, new FakeMedia());

        Assert.False(service.CanRead);
        Assert.NotEmpty(service.UnavailableReason);

        var outcome = await service.FramesAsync(ItemId, [0]);

        Assert.NotNull(outcome.Error);
        Assert.Contains("未连接", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(source.FrameCalls);
    }

    // ───────────── 辅助 ─────────────

    private static VideoReadService CreateService(
        FakeSource source, FakeMedia media, DouyinVideoOptions? options = null) =>
        new(source, media, options ?? new DouyinVideoOptions(), Logger);

    /// <summary>造一段合法的 16 kHz 单声道 PCM16 静音 WAV（时长按参数）。</summary>
    private static byte[] SilenceWav(int durationMs)
    {
        const int sampleRate = 16_000;
        var samples = sampleRate * durationMs / 1000;
        var dataBytes = samples * 2;
        var wav = new byte[44 + dataBytes];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + dataBytes).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(sampleRate * 2).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(dataBytes).CopyTo(wav, 40);

        return wav;
    }

    private sealed class FakeSource : IDouyinMediaSource
    {
        public bool Ready { get; init; } = true;

        public long DurationMs { get; init; } = 28_000;

        public bool BrokenAudio { get; init; }

        public bool IsReady => Ready;

        public string NotReadyReason => Ready ? string.Empty : "抖音未连接：浏览器底座还没连上 /ws/channel。";

        public List<(long Start, long End)> AudioCalls { get; } = [];

        public List<List<long>> FrameCalls { get; } = [];

        public Task<DouyinVideoInfo> ResolveVideoAsync(
            string itemId, int timeoutMs, CancellationToken ct = default) =>
            Task.FromResult(new DouyinVideoInfo(itemId, "测试视频", DurationMs, ["https://x/a.mp4"]));

        public Task<DouyinVideoFrames> CaptureFramesAsync(
            string itemId,
            IReadOnlyList<long> timesMs,
            int maxWidth,
            int maxVideoBytes,
            int timeoutMs,
            CancellationToken ct = default)
        {
            FrameCalls.Add([.. timesMs]);
            var frames = timesMs
                .Select((t, index) => new DouyinVideoFrame(index, t, [1, 2, 3]))
                .ToList();

            return Task.FromResult(new DouyinVideoFrames(DurationMs, maxWidth, Math.Max(1, (int)(DurationMs / 100)), frames));
        }

        public Task<DouyinAudioSlice> ExtractAudioAsync(
            string itemId,
            long startMs,
            long endMs,
            int maxVideoBytes,
            int timeoutMs,
            CancellationToken ct = default)
        {
            AudioCalls.Add((startMs, endMs));

            if (BrokenAudio)
            {
                return Task.FromResult(new DouyinAudioSlice([9, 9, 9, 9], 16_000, startMs, endMs, DurationMs));
            }

            return Task.FromResult(new DouyinAudioSlice(
                SilenceWav((int)(endMs - startMs)),
                16_000,
                startMs,
                endMs,
                DurationMs));
        }
    }

    private sealed class FakeMedia : IDeepSeekMediaClient
    {
        public bool AsrAvailable { get; init; } = true;

        public string Text { get; init; } = "第一句\n第二句";

        public IReadOnlyList<AsrSegment> Parts { get; init; } =
        [
            new AsrSegment(0, 3_200, "第一句"),
            new AsrSegment(3_200, 9_800, "第二句"),
        ];

        public List<int> AsrCalls { get; } = [];

        public int LastWavDurationMs { get; private set; }

        public bool OcrAvailable => false;

        public bool TtsAvailable => false;

        public Task<OcrResult> OcrAsync(
            byte[] file, string fileName, OcrPurpose purpose, CancellationToken ct = default) =>
            throw new NotSupportedException("本测试不测 OCR。");

        public Task<TtsResult> TtsAsync(string text, string? voiceId = null, CancellationToken ct = default) =>
            throw new NotSupportedException("本测试不测 TTS。");

        public Task<AsrResult> AsrAsync(byte[] audio, string fileName, CancellationToken ct = default)
        {
            AsrCalls.Add(audio.Length);

            Assert.True(WavSlicer.TryParse(audio, out var info));
            LastWavDurationMs = (int)info!.DurationMs;

            return Task.FromResult(new AsrResult(Text, info.DurationMs, Parts));
        }
    }
}
