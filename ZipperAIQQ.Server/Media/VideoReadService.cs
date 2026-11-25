using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Server.Ai.DeepSeekTools;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Server.Media;

/// <summary>
/// 视频理解的实现：底座抽帧 / 抽音 → 服务端拼音频 → ASR（B4）。
/// </summary>
/// <remarks>
/// <para>
/// 分工：<see cref="IDouyinMediaSource"/> 负责「跟浏览器要东西」（帧图、20 秒音频窗口），
/// 本类负责「业务上的取舍」——时间点校验、窗口循环、拼回整段、调 ASR、把时间戳对齐到视频。
/// </para>
/// <para>
/// **为什么窗口循环放服务端**：`/ws/channel` 单帧上限 1 MiB，3 分钟 WAV 一次塞不下；
/// 而窗口多长、要不要继续，是策略（配置 <c>douyin.videoAudioWindowSeconds</c> /
/// <c>videoMaxAudioSeconds</c>），策略归服务端。
/// </para>
/// <para>
/// **只用 DeepSeek ASR**：用户 2026-11-20 定「不跑本地引擎」——失败就说「转写不可用 + 原因」，
/// 不回落本地模型（本机也没装）。
/// </para>
/// </remarks>
public sealed class VideoReadService : IVideoReader
{
    /// <summary>单次抽帧的上限（与 <see cref="DouyinVideoOptions.MaxFrames"/> 取小）。</summary>
    private const int HardMaxFrames = 16;

    /// <summary>帧宽上限（与配置取小）：再大就把回传帧挤爆 1 MiB。</summary>
    private const int HardMaxFrameWidth = 1080;

    /// <summary>转写时长硬顶（秒）：再多就不是「看一眼视频」而是转录整部片子了。</summary>
    private const int HardMaxAudioSeconds = 600;

    /// <summary>
    /// 抽音窗口的**可用**上限（秒）。
    /// </summary>
    /// <remarks>
    /// 不是拍脑袋：16 kHz 单声道一秒 32,000 字节，`/ws/channel` 单帧 1 MiB，
    /// base64 膨胀 4/3，再留 JSON 余量 ⇒ 20 s 是安全值、21 s 顶到线。
    /// 配成 22–60 会让**每一次转写都失败**（sidecar 回 too_large），所以这里硬夹；
    /// 夹到了就在启动日志里说一句 —— 配置写了个做不到的数字，不能悄悄改掉。
    /// </remarks>
    private const int MaxAudioWindowSeconds = 21;

    /// <summary>ASR 本身不可用时的原因（与 <see cref="UnavailableReason"/> 区分：底座是好的，缺的是 ASR）。</summary>
    private const string AsrUnavailableReason =
        "转写不可用：DeepSeek ASR 没开或没有可用的 key（抽帧/看画面不受影响）。";

    private readonly IDouyinMediaSource _source;
    private readonly IDeepSeekMediaClient _media;
    private readonly DouyinVideoOptions _options;
    private readonly ILogger _logger;

    public VideoReadService(
        IDouyinMediaSource source,
        IDeepSeekMediaClient media,
        DouyinVideoOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _media = media;
        _options = options;
        _logger = logger;

        if (options.AudioWindowSeconds > MaxAudioWindowSeconds)
        {
            _logger.Warning(
                "douyin.videoAudioWindowSeconds={Configured} 超过可用上限 {Max} 秒（1 MiB 单帧限制），已按 {Max} 秒工作",
                options.AudioWindowSeconds, MaxAudioWindowSeconds, MaxAudioWindowSeconds);
        }
    }

    /// <inheritdoc />
    public bool CanRead => _options.Enabled && _source.IsReady;

    /// <inheritdoc />
    public bool CanTranscribe => CanRead && _media.AsrAvailable;

    /// <inheritdoc />
    public string UnavailableReason
    {
        get
        {
            if (!_options.Enabled)
            {
                return "视频理解没开（douyin.videoEnabled=false）。";
            }

            return _source.IsReady ? string.Empty : _source.NotReadyReason;
        }
    }

    /// <inheritdoc />
    public async Task<VideoBrief> LocateAsync(string itemId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        var info = await _source
            .ResolveVideoAsync(itemId, TimeoutMs, ct)
            .ConfigureAwait(false);

        return new VideoBrief(info.ItemId, info.Title, info.DurationMs);
    }

    /// <inheritdoc />
    public async Task<VideoFramesOutcome> FramesAsync(
        string itemId, IReadOnlyList<long> timesMs, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(timesMs);

        if (!CanRead)
        {
            return VideoFramesOutcome.Failed(UnavailableReason);
        }

        var cap = Math.Clamp(_options.MaxFrames, 1, HardMaxFrames);
        var times = NormalizeTimes(timesMs, cap);
        if (times.Count == 0)
        {
            return VideoFramesOutcome.Failed("没有可用的抽帧时间点。");
        }

        var width = Math.Clamp(_options.FrameMaxWidth, 120, HardMaxFrameWidth);

        try
        {
            var batch = await _source
                .CaptureFramesAsync(itemId, times, width, MaxVideoBytes, TimeoutMs, ct)
                .ConfigureAwait(false);

            var frames = batch.Frames
                .Select(f => new VideoFrameImage(f.Index, f.AtMs, f.Jpeg, "image/jpeg"))
                .ToList();

            var note = $"视频时长 {VideoTools.FormatTime(batch.DurationMs)}，画面已缩到 {batch.Width}×{batch.Height}。";
            if (times.Count < timesMs.Count)
            {
                note += $"（一次最多给 {cap} 张，多余的没截）";
            }

            return new VideoFramesOutcome(frames, note, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 失败要响：日志留成因，模型那边拿到人话（它要据此换视频或改用转写）
            _logger.Warning(ex, "抽帧失败：itemId={ItemId} 张数={Count}", itemId, times.Count);
            return VideoFramesOutcome.Failed(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<VideoTranscriptOutcome> TranscribeAsync(
        string itemId, long startMs, long endMs, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        if (!CanTranscribe)
        {
            return VideoTranscriptOutcome.Failed(
                CanRead ? AsrUnavailableReason : UnavailableReason);
        }

        long total;
        try
        {
            // 要时长才能算「到结尾」与上限；取不到就按调用方给的区间走
            var brief = await LocateAsync(itemId, ct).ConfigureAwait(false);
            total = brief.DurationMs;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "转写前取视频详情失败：itemId={ItemId}", itemId);
            return VideoTranscriptOutcome.Failed($"取不到这条视频的详情：{ex.Message}");
        }

        var from = Math.Max(0, startMs);

        // 起点已经越过视频末尾时，别算出负数再回一句「区间只有 -140000 毫秒，太短了」
        if (total > 0 && from >= total)
        {
            return VideoTranscriptOutcome.Failed(
                $"起点（{VideoTools.FormatTime(from)}）已经超过视频时长（{VideoTools.FormatTime(total)}）。");
        }

        var limitMs = Math.Clamp(_options.MaxAudioSeconds, 10, HardMaxAudioSeconds) * 1000L;
        var to = endMs > from ? endMs : (total > 0 ? total : from + limitMs);

        if (total > 0 && to > total)
        {
            to = total;
        }

        var truncated = false;
        if (to - from > limitMs)
        {
            to = from + limitMs;
            truncated = true;
        }

        if (to - from < 1000)
        {
            return VideoTranscriptOutcome.Failed(
                $"要转写的区间只有 {to - from} 毫秒，太短了（至少 1 秒）。");
        }

        var windowMs = Math.Clamp(_options.AudioWindowSeconds, 5, MaxAudioWindowSeconds) * 1000L;
        var pcm = new MemoryStream();
        WavInfo? template = null;
        var reachedMs = from;

        try
        {
            for (var cursor = from; cursor < to; cursor += windowMs)
            {
                ct.ThrowIfCancellationRequested();

                var sliceEnd = Math.Min(to, cursor + windowMs);
                var slice = await _source
                    .ExtractAudioAsync(itemId, cursor, sliceEnd, MaxVideoBytes, TimeoutMs, ct)
                    .ConfigureAwait(false);

                if (!WavSlicer.TryParse(slice.Wav, out var info) || info is null)
                {
                    return VideoTranscriptOutcome.Failed("底座回的音频不是合法 PCM WAV（可能抽音失败）。");
                }

                template ??= info;
                pcm.Write(slice.Wav, info.DataOffset, info.DataLength);
                reachedMs = Math.Max(reachedMs, slice.EndMs);

                // 音频比视频短（有些作品结尾是静音/黑屏）：按真实长度收工，别再去要空气
                if (slice.DurationMs > 0 && to > slice.DurationMs && reachedMs >= slice.DurationMs)
                {
                    to = Math.Min(to, slice.DurationMs);
                    break;
                }
            }

            if (template is null || pcm.Length == 0)
            {
                return VideoTranscriptOutcome.Failed(
                    "这条视频没有抽到音轨（可能只有画面，或者抽音失败）。");
            }

            var wav = WavSlicer.BuildWav(template, pcm.ToArray());
            var result = await _media.AsrAsync(wav, $"{itemId}.wav", ct).ConfigureAwait(false);

            // ASR 的段级时间戳是相对**这段音频**的，加上区间起点才是视频里的绝对位置
            var lines = result.Parts
                .Select(p => new VideoTranscriptLine(from + p.StartMs, from + p.EndMs, p.Text))
                .ToList();

            var note = BuildTranscriptNote(from, reachedMs, total, truncated, lines.Count, result.Text);
            return new VideoTranscriptOutcome(lines, result.Text, result.DurationMs, note, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "转写失败：itemId={ItemId} 区间={From}-{To}ms", itemId, from, to);
            return VideoTranscriptOutcome.Failed(ex.Message);
        }
    }

    private int TimeoutMs => Math.Clamp(_options.TimeoutSeconds, 5, 120) * 1000;

    private int MaxVideoBytes => Math.Clamp(_options.MaxVideoMb, 1, 96) * 1024 * 1024;

    /// <summary>时间点清洗：去掉负数、排序、去重，超出上限的截掉。</summary>
    /// <param name="timesMs">调用方给的时间点。</param>
    /// <param name="cap">最多保留几个。</param>
    private static List<long> NormalizeTimes(IReadOnlyList<long> timesMs, int cap)
    {
        var result = new List<long>(Math.Min(timesMs.Count, cap));
        foreach (var t in timesMs.Select(t => Math.Max(0, t)).Order())
        {
            if (result.Count >= cap)
            {
                break;
            }

            // 相邻 1 ms 内的重复点没有意义（视频抽两帧几乎一样的画面）
            if (result.Count > 0 && t - result[^1] < 1)
            {
                continue;
            }

            result.Add(t);
        }

        return result;
    }

    private static string BuildTranscriptNote(
        long fromMs, long reachedMs, long totalMs, bool truncated, int lineCount, string text)
    {
        var parts = new List<string>
        {
            $"区间 {VideoTools.FormatTime(fromMs)}–{VideoTools.FormatTime(reachedMs)}",
        };

        if (totalMs > 0)
        {
            parts.Add($"视频全长 {VideoTools.FormatTime(totalMs)}");
        }

        if (truncated)
        {
            parts.Add("超过单次转写上限，只转了这一段的前半部分（想听后面请用 start/end 指定区间）");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            parts.Add("这段没有识别到人声（可能是纯音乐或没人说话）");
        }
        else if (lineCount == 0)
        {
            parts.Add("平台给了文本但没有分段，时间戳不可用");
        }

        return string.Join('，', parts) + "。";
    }
}
