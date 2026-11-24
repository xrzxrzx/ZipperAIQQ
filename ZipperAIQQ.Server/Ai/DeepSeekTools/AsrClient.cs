using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Server.Ai;

namespace ZipperAIQQ.Server.Ai.DeepSeekTools;

/// <summary>
/// ASR：<c>POST {base}/v1/tools/asr</c>（multipart：<c>file</c> → <c>{text, duration_ms}</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>切片是硬约束</b>（方案 G6）：平台单次 **≤120 秒 / ≤8 MiB**，两条**同时**要满足。
/// 踩过一次坑：只按时长切时，60 秒的 44.1 kHz 立体声录音就有 **10 MiB**，一段就超字节上限，
/// 平台 413、整次失败 —— 所以这里把字节上限**折算成秒**再取较小值，切完还逐段复查。
/// </para>
/// <para>
/// 拿不到 PCM 头（ogg / opus / mp3）时切不了，只能整段发；这时如果超过 8 MiB，报错要说人话：
/// 「这个格式我切不了，请先转成 16 kHz 单声道 WAV」——而不是含糊地说"太大"。
/// </para>
/// <para>
/// 重叠的代价是边界那句话会**重复出现**：宁可重复几个字，也不要掉半句 ——
/// 上层（B4 的字幕/摘要）对着重复文本还能理解，对着缺字只能猜。
/// </para>
/// <para>
/// 失败语义：任一段失败**整次失败**（不返回"半截转写"假装成功）。
/// 半截文本最容易被当成完整内容引用，比空结果更危险。
/// </para>
/// </remarks>
public sealed class AsrClient
{
    private const string AsrPath = "/v1/tools/asr";

    /// <summary>单段时长上限：比平台 120 s 更保守，留出重采样/头部的余量。</summary>
    private const double SliceSeconds = 110;

    /// <summary>段间重叠：防「一句话被切成两半」。</summary>
    private const double OverlapSeconds = 1.0;

    /// <summary>平台单文件字节上限。</summary>
    private const int PlatformMaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 留给 multipart 头/边界的余量比例：8 MiB 是**文件**上限，
    /// 而请求体里还有 boundary 与字段头，所以按 88% 折算。
    /// </summary>
    private const double MultipartHeadroom = 0.88;

    private readonly HttpClient _http;
    private readonly IDeepSeekKeyProvider _keys;
    private readonly AiOptions _options;
    private readonly ILogger _logger;

    public AsrClient(HttpClient http, IDeepSeekKeyProvider keys, AiOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _keys = keys;
        _options = options;
        _logger = logger;
    }

    public async Task<AsrResult> AsrAsync(
        byte[] audio, string fileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (audio.Length == 0)
        {
            throw new DeepSeekMediaException(DeepSeekMediaErrorKind.Usage, "ASR：音频是空的");
        }

        var planned = Plan(audio, fileName);

        var parts = new List<AsrSegment>(planned.Count);
        var cursorMs = 0L;

        foreach (var part in planned)
        {
            ct.ThrowIfCancellationRequested();

            var (text, partDurationMs) = await TranscribeOneAsync(
                part.Bytes, part.Name, part.Index, planned.Count, ct).ConfigureAwait(false);

            // 有真实切片边界就用它；非 PCM 整段发时按平台回的分段时长累加
            var start = part.HasTimeline ? part.StartMs : cursorMs;
            var end = part.HasTimeline ? part.EndMs : start + partDurationMs;

            parts.Add(new AsrSegment(start, end, text));
            cursorMs = end;
        }

        // 时长优先用**原始音频**的真实时长（切片重叠会让分段时长加起来偏大）
        var timelineKnown = planned.Count > 0 && planned[0].HasTimeline;
        var totalDuration = timelineKnown ? planned[^1].EndMs : cursorMs;

        var joined = string.Join('\n', parts.Select(p => p.Text).Where(t => t.Length > 0));
        _logger.Information(
            "ASR 完成：文件={File} 段={Parts} 字节={Bytes} 时长={DurationMs}ms 字数={Chars}",
            fileName, parts.Count, audio.Length, totalDuration, joined.Length);

        return new AsrResult(joined, totalDuration, parts);
    }

    /// <summary>
    /// 决定怎么切：PCM 能切 → 同时满足「≤110 s」与「≤8 MiB」（字节上限折算成秒）；
    /// 切不了 → 整段发，但会明确告诉调用方这颗文件没有被切开过。
    /// </summary>
    private List<PlannedPart> Plan(byte[] audio, string fileName)
    {
        var byteCap = Math.Min(PlatformMaxBytes, _options.MaxUploadBytes);

        if (WavSlicer.TryParse(audio, out var info) && info is not null)
        {
            // 两个平台上限都要判：**时长 ≤120 s** 与字节 ≤8 MiB。只判字节的话，
            // 一条 5 分钟、4 MB 的录音能过体积关，却会在平台侧因时长被拒。
            var needsSlice = info.DurationMs > SliceSeconds * 1000 || audio.Length > byteCap;

            if (!needsSlice)
            {
                // 整段就合规 → 先重打包：录音软件常塞封面/歌词块，音频几十毫秒文件却有 9 MiB，
                // 不丢元数据就会被平台按体积拒掉（而实际音频完全没问题）。
                return
                [
                    new PlannedPart(
                        WavSlicer.Repack(audio, info), fileName, 1,
                        StartMs: 0, EndMs: info.DurationMs, HasTimeline: true)
                ];
            }

            // 字节上限折算成秒：44.1 kHz 立体声每秒 176,400 字节，110 s 就是 19 MiB —— 远超 8 MiB
            var secondsByBytes = info.BytesPerSecond > 0
                ? byteCap * MultipartHeadroom / info.BytesPerSecond
                : SliceSeconds;

            var effectiveSeconds = Math.Min(SliceSeconds, secondsByBytes);
            if (effectiveSeconds < 1)
            {
                throw new DeepSeekMediaException(
                    DeepSeekMediaErrorKind.TooLarge,
                    $"ASR：音频码率过高（每秒 {info.BytesPerSecond} 字节），切不到 1 秒以下仍超 {byteCap} 字节上限");
            }

            // 段越短，1 秒重叠占比越大（1.2 s 的段几乎被读两遍 → 请求数翻十倍、还把同一句话记账两次）：
            // 重叠压到段长的 1/4 以内。
            var overlap = Math.Min(OverlapSeconds, effectiveSeconds / 4);
            var slices = WavSlicer.Slice(audio, info, effectiveSeconds, overlap);

            var result = new List<PlannedPart>(slices.Count);
            for (var i = 0; i < slices.Count; i++)
            {
                // 折算是保守的，这里再复查一次：宁可本地拒，也不发一个注定 413 的请求
                if (slices[i].Bytes.Length > byteCap)
                {
                    throw new DeepSeekMediaException(
                        DeepSeekMediaErrorKind.TooLarge,
                        $"ASR：第 {i + 1} 段仍有 {slices[i].Bytes.Length} 字节，超过平台 {byteCap} 字节上限");
                }

                result.Add(new PlannedPart(
                    slices[i].Bytes, $"part{i + 1:D2}.wav", i + 1,
                    slices[i].StartMs, slices[i].EndMs, HasTimeline: true));
            }

            return result;
        }

        if (audio.Length > byteCap)
        {
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.TooLarge,
                $"ASR：这个格式切不了（不是 PCM WAV），且 {audio.Length} 字节超过 {byteCap} 字节上限；" +
                "请先转成 16 kHz 单声道 WAV");
        }

        return [new PlannedPart(audio, fileName, 1, 0, 0, HasTimeline: false)];
    }

    private async Task<(string Text, long DurationMs)> TranscribeOneAsync(
        byte[] bytes, string partName, int index, int total, CancellationToken ct)
    {
        using var content = MediaRequest.Multipart(bytes, "file", partName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, _options.BaseUrl.TrimEnd('/') + AsrPath)
        {
            Content = content,
        };

        using var response = await MediaRequest
            .SendAsync(_http, _keys, request, _logger, $"ASR（第 {index}/{total} 段）", ct)
            .ConfigureAwait(false);

        var json = await MediaRequest.ReadStringAsync(response, _logger, "ASR", ct).ConfigureAwait(false);
        return ParsePart(json, index, total);
    }

    /// <summary>解析单段：<c>{text, duration_ms}</c>（实测口径）；缺 text 视为 Parse。</summary>
    internal (string Text, long DurationMs) ParsePart(string json, int index, int total)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.Error("ASR 第 {Index}/{Total} 段响应不是合法 JSON：{Snippet}", index, total, Truncate(json));
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Parse, "ASR 响应不是合法 JSON", inner: ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var text = MediaRequest.StringOrNull(root, "text", "transcript");
            if (text is null)
            {
                _logger.Error("ASR 第 {Index}/{Total} 段响应里没有 text：{Snippet}", index, total, Truncate(json));
                throw new DeepSeekMediaException(
                    DeepSeekMediaErrorKind.Parse, "ASR 响应里没有 text");
            }

            var duration = MediaRequest.LongOrNull(root, "duration_ms", "durationMs") ?? 0;
            return (text, duration);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500];

    /// <summary>一段待发送的音频（<see cref="HasTimeline"/> = 是否知道它在原始音频里的位置）。</summary>
    private sealed record PlannedPart(
        byte[] Bytes, string Name, int Index, long StartMs, long EndMs, bool HasTimeline);
}
