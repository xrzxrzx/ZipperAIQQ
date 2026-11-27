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
    private readonly ILogger _logger;

    public AsrSpeechTranscriber(
        AiOptions options,
        IDeepSeekMediaClient media,
        IAudioSource source,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _media = media;
        _source = source;
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
            var wav = await _source
                .GetWavAsync(clip.FileId, clip.FilePath, ct)
                .ConfigureAwait(false);

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
}
