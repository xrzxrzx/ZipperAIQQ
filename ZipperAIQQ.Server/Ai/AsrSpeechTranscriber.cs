using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Channel.Audio;

namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// 外部 ASR 转写（B00001）：NapCat 自带的 <c>fetch_ptt_text</c> 转不出来时的回退路线。
/// </summary>
/// <remarks>
/// <para>
/// 链路：<see cref="IAudioSource"/> 取 **WAV 字节**（平台侧把 <c>SILK_V3</c> 解成 PCM）
/// → B2 的 <see cref="IDeepSeekMediaClient.AsrAsync"/>（切片由它自己负责）。
/// </para>
/// <para>
/// 为什么不在 Server 里自己转码：QQ 语音是 <c>SILK_V3</c>，而本机的 ffmpeg 是坏的
/// （2026-11-28 实测 <c>not a valid application for this OS platform</c>）；
/// NapCat 的 <c>get_record + out_format:"wav"</c> 反而能 361 ms 出 WAV。
/// **转码交给本来就懂这个格式的那一端**，这里只管调 ASR。
/// </para>
/// <para>
/// 放在 Server 而不是 Channel：ASR 要 API key，而 key 只在 Server 解得出
/// （与 <c>IWebSearchProvider</c> 同一条依赖方向）。
/// </para>
/// <para>
/// 失败一律返回 <c>null</c>、**不抛**：语音转不出来不该让整条消息丢失或让接收循环报错。
/// 原因写日志，便于区分「取音频失败 / 超上限 / 接口失败」。
/// </para>
/// </remarks>
public sealed class AsrSpeechTranscriber : ISpeechTranscriber
{
    private readonly AiOptions _options;
    private readonly IDeepSeekMediaClient _media;
    private readonly IAudioSource _source;
    private readonly IAudioTranscoder _transcoder;
    private readonly ISilkDecoder _decoder;
    private readonly SpeechOptions _speech;
    private readonly IHttpClientFactory _http;
    private readonly ILogger _logger;

    public AsrSpeechTranscriber(
        AiOptions options,
        IDeepSeekMediaClient media,
        IAudioSource source,
        IAudioTranscoder transcoder,
        ISilkDecoder decoder,
        SpeechOptions speech,
        IHttpClientFactory http,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(transcoder);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _media = media;
        _source = source;
        _transcoder = transcoder;
        _decoder = decoder;
        _speech = speech;
        _http = http;
        _logger = logger;
    }

    /// <summary>ASR 开关开着、且客户端认为可用（有 key / 基地址合法）。</summary>
    public bool IsAvailable => _options.AsrEnabled && _media.AsrAvailable;

    public async Task<string?> TranscribeAsync(AudioClip clip, CancellationToken ct = default)
    {
        if (!IsAvailable || clip is null)
        {
            return null;
        }

        try
        {
            var wav = await FetchWavAsync(clip, ct).ConfigureAwait(false);

            if (wav is null || wav.Length == 0)
            {
                _logger.Warning("取不到语音音频（file={File}），ASR 放弃", clip.FileId);
                return null;
            }

            if (wav.Length > _options.MaxUploadBytes)
            {
                _logger.Warning(
                    "语音音频超上限（{Bytes} > {Max}），ASR 放弃：file={File}",
                    wav.Length, _options.MaxUploadBytes, clip.FileId);
                return null;
            }

            var result = await _media.AsrAsync(wav, "voice.wav", ct).ConfigureAwait(false);
            var text = result.Text?.Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.Information(
                    "ASR 返回空文本（file={File}，{DurationMs}ms）", clip.FileId, result.DurationMs);
                return null;
            }

            return text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (DeepSeekMediaException ex)
        {
            // 分类由 B2 的客户端给（Unavailable / Auth / TooLarge / RateLimited / Timeout …）
            _logger.Warning(ex, "ASR 接口失败（file={File}，kind={Kind}）", clip.FileId, ex.Kind);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ASR 转写异常（file={File}）", clip.FileId);
            return null;
        }
    }

    /// <summary>
    /// 取音频（带重试）：**先走网络（QQ 的下载地址），NapCat 转码只作兜底**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用户 2026-11-28 明确要求「不走本地」：不读 NapCat 那台机器上的 <c>record.data.path</c>。
    /// 改成从 <c>record.data.url</c> 下载 —— 它同样是"消息到达即有效"，且**不绑定部署机器**。
    /// </para>
    /// <para>
    /// 但 ASR 接口只收 <b>16 位 PCM WAV 或 Ogg Opus</b>（[官方指南](https://api-docs.deepseek.com/zh-cn/guides/asr/)，
    /// 按文件内容判断），而 QQ 语音是 SILK —— 所以中间**必须有一次转码**，这不是选择：
    /// 本机 ffmpeg 是坏的，于是只有两条路：本地解码器（silk-wasm）或 NapCat 的 <c>get_record</c>。
    /// 这里用前者（快、不依赖 QQ 消息索引），后者作为兜底。
    /// </para>
    /// </remarks>
    private async Task<byte[]?> FetchWavAsync(AudioClip clip, CancellationToken ct)
    {
        var attempts = Math.Max(1, _speech.AudioFetchAttempts);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var audio = await DownloadAsync(clip, ct).ConfigureAwait(false)
                        ?? await _source.GetAudioAsync(clip.FileId, null, ct).ConfigureAwait(false);

            if (audio is { Bytes.Length: > 0 })
            {
                if (IsWav(audio.Bytes))
                {
                    return audio.Bytes;
                }

                // ① 先 ffmpeg：URL 下载到的是 **AMR**，它能转（实测 Server 2016 上 ffmpeg-static 可用）
                var wav = await _transcoder.ToWavAsync(audio.Bytes, audio.Format, ct).ConfigureAwait(false);

                // ② 再试 silk-wasm：ffmpeg 解不了 SILK（Invalid frame size），由它兜底
                if (wav is null)
                {
                    wav = await _decoder.ToWavAsync(audio.Bytes, ct).ConfigureAwait(false);
                }

                if (wav is { Length: > 44 })
                {
                    _logger.Information(
                        "语音已转码（{Format} {In} 字节 → WAV {Out} 字节，file={File}）",
                        audio.Format ?? "?", audio.Bytes.Length, wav.Length, clip.FileId);
                    return wav;
                }

                _logger.Warning("语音转码失败（{Bytes} 字节，file={File}）", audio.Bytes.Length, clip.FileId);
                return null;
            }

            if (attempt >= attempts)
            {
                break;
            }

            _logger.Information(
                "取语音音频失败，重试 {Attempt}/{Total}（file={File}）", attempt + 1, attempts, clip.FileId);

            try
            {
                await Task.Delay(Math.Max(0, _speech.AudioFetchRetryDelayMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>从 QQ 的下载地址取原始音频（网络，不碰本地文件）。</summary>
    private async Task<AudioBytes?> DownloadAsync(AudioClip clip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clip.Url) ||
            !Uri.TryCreate(clip.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            var http = _http.CreateClient(nameof(AsrSpeechTranscriber));
            http.Timeout = TimeSpan.FromSeconds(Math.Max(10, _options.TimeoutSeconds));

            using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning(
                    "语音下载失败（{Status}）：file={File}", (int)response.StatusCode, clip.FileId);
                return null;
            }

            if (response.Content.Headers.ContentLength is { } length && length > _options.MaxUploadBytes)
            {
                _logger.Warning("语音体积超上限（{Bytes} > {Max}），已放弃", length, _options.MaxUploadBytes);
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (data.Length == 0 || data.Length > _options.MaxUploadBytes)
            {
                return null;
            }

            return new AudioBytes(data, clip.Format);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "语音下载异常（file={File}）", clip.FileId);
            return null;
        }
    }

    /// <summary>是不是已经是 WAV（看魔数，不信扩展名）。</summary>
    private static bool IsWav(byte[] bytes) =>
        bytes.Length > 44 &&
        bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
        bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E';
}
