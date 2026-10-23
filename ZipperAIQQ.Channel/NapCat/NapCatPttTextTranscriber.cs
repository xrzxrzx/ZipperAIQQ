using System.Text.Json.Nodes;
using Serilog;
using ZipperAIQQ.Channel.Audio;

namespace ZipperAIQQ.Channel.NapCat;

/// <summary>
/// NapCat 自带的语音转文字（<c>fetch_ptt_text</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么走这条路：需求 FR-1.5 要求语音在通道层转文本；
/// NapCat 扩展 API 里已有 <c>fetch_ptt_text</c>，比本地 ASR 模型轻得多（用户 Q5 定稿）。
/// </para>
/// <para>
/// **可用性探测不额外发请求**：直接尝试真实语音；
/// 连续失败 <see cref="SpeechOptions.MaxConsecutiveFailures"/> 次即判定平台不支持，
/// 之后不再尝试（每条语音都白等一次超时是不可接受的）。
/// 判定结果与原因都会进日志，便于确认 NapCat 版本是否支持。
/// </para>
/// </remarks>
public sealed class NapCatPttTextTranscriber : ISpeechTranscriber
{
    /// <summary>NapCat 的语音转文字 action 名。</summary>
    public const string ActionName = "fetch_ptt_text";

    private readonly Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> _callApi;
    private readonly SpeechOptions _options;
    private readonly ILogger _logger;

    private int _consecutiveFailures;
    private volatile bool _available = true;

    public NapCatPttTextTranscriber(
        Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> callApi,
        SpeechOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(callApi);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _callApi = callApi;
        _options = options;
        _logger = logger;
    }

    public bool IsAvailable => _available && _options.Enabled;

    public async Task<string?> TranscribeAsync(AudioClip clip, CancellationToken ct = default)
    {
        if (!IsAvailable || clip is null || string.IsNullOrWhiteSpace(clip.FileId))
        {
            return null;
        }

        try
        {
            var data = await _callApi(
                ActionName,
                new JsonObject { ["file"] = clip.FileId },
                ct).ConfigureAwait(false);

            var text = ExtractText(data);
            if (text is null)
            {
                OnFailure();
                return null;
            }

            Interlocked.Exchange(ref _consecutiveFailures, 0);
            return text.Length == 0 ? null : text;
        }
        catch (OperationCanceledException)
        {
            // 取消不算失败（通常是停机 / 超时链路），保持可用
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "语音转写异常（file={File}）", clip.FileId);
            OnFailure();
            return null;
        }
    }

    private void OnFailure()
    {
        var count = Interlocked.Increment(ref _consecutiveFailures);
        if (count < Math.Max(1, _options.MaxConsecutiveFailures))
        {
            return;
        }

        _available = false;
        _logger.Warning(
            "语音转写连续失败 {Count} 次，已禁用（当前 NapCat 可能不支持 {Action}；" +
            "语音将保留 [语音] 占位，检查 NapCat 版本后重启即可重试）",
            count, ActionName);
    }

    /// <summary>
    /// 从返回里取文本：兼容 <c>{"text": "..."}</c>、纯字符串、以及 <c>{"data": {...}}</c> 包装。
    /// </summary>
    private static string? ExtractText(JsonNode? data) => data switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text.Trim(),
        JsonObject obj when obj["text"] is JsonValue text && text.TryGetValue<string>(out var t) => t.Trim(),
        JsonObject obj when obj["data"] is JsonValue inner && inner.TryGetValue<string>(out var d) => d.Trim(),
        JsonObject obj when obj["data"] is JsonObject innerObj => ExtractText(innerObj),
        _ => null,
    };
}
