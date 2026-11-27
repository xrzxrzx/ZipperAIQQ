using Serilog;

namespace ZipperAIQQ.Channel.Audio;

/// <summary>
/// 两条转写路线串起来：**先便宜的后贵**（B00001）。
/// </summary>
/// <remarks>
/// <para>
/// 顺序是刻意的：NapCat 自带的 <c>fetch_ptt_text</c> 走 QQ 侧现成结果 —— 免费、无需下载音频；
/// 它转不出来（QQ 侧 ASR 卡死 / 无响应）才回退到外部 ASR（下载音频 + 一次付费接口）。
/// 反过来做等于每条语音都花钱，而且慢。
/// </para>
/// <para>
/// 「主路线返回 <c>null</c>」有很多原因（不支持 / 超时 / 没有 message_id），这里**不区分**：
/// 对回退路线来说都只有一个意思 —— 「你上，试试看」。
/// 但主路线**熔断中**（<see cref="ISpeechTranscriber.IsAvailable"/> 为 false 且原因不是配置关闭）
/// 时不再浪费一次等待，直接走回退。
/// </para>
/// </remarks>
public sealed class FallbackSpeechTranscriber : ISpeechTranscriber
{
    private readonly ISpeechTranscriber _primary;
    private readonly ISpeechTranscriber _fallback;
    private readonly ILogger _logger;

    public FallbackSpeechTranscriber(
        ISpeechTranscriber primary, ISpeechTranscriber fallback, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(logger);

        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    /// <summary>任一可用即可尝试。</summary>
    public bool IsAvailable => _primary.IsAvailable || _fallback.IsAvailable;

    public async Task<string?> TranscribeAsync(AudioClip clip, CancellationToken ct = default)
    {
        if (clip is null)
        {
            return null;
        }

        if (_primary.IsAvailable)
        {
            var text = await _primary.TranscribeAsync(clip, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            _logger.Information(
                "NapCat 转写没拿到文本（file={File}），回退外部 ASR", clip.FileId);
        }

        if (!_fallback.IsAvailable)
        {
            return null;
        }

        var fallbackText = await _fallback.TranscribeAsync(clip, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fallbackText))
        {
            _logger.Information("外部 ASR 已转写（file={File}，{Length} 字）", clip.FileId, fallbackText.Length);
        }

        return string.IsNullOrWhiteSpace(fallbackText) ? null : fallbackText;
    }
}
