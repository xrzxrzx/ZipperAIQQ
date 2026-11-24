using System.Globalization;
using System.Net.Http.Json;
using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Server.Ai;

namespace ZipperAIQQ.Server.Ai.DeepSeekTools;

/// <summary>
/// TTS：<c>POST {base}/v1/tools/tts</c>（JSON：<c>text</c> / <c>voice_id</c> / <c>format</c> / <c>filter_markdown</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 产物**只落文件**（问题记录 06 Q-07 拍板）：本类不碰任何通道，也不发语音消息。
/// 文件名带时间戳与短随机串，避免同一秒两次调用互相覆盖。
/// </para>
/// <para>
/// 平台限制：<c>text</c> ≤10000 **计费字符**、请求体 ≤64 KiB。超了在本地就拒，
/// 不让一次超长文本把整轮对话拖到超时。
/// </para>
/// <para>
/// 响应是**二进制音频**（实测 <c>audio/wav</c>）。若平台偶尔回 JSON 错误体，这里按 Parse 处理并记片段 ——
/// 把 JSON 当音频写到磁盘上才是最坏的结果（生成一个打不开的文件，还报告成功）。
/// </para>
/// </remarks>
public sealed class TtsClient
{
    private const string TtsPath = "/v1/tools/tts";

    /// <summary>平台口径的计费字符上限。</summary>
    private const int MaxCharacters = 10_000;

    private readonly HttpClient _http;
    private readonly IDeepSeekKeyProvider _keys;
    private readonly AiOptions _options;
    private readonly ILogger _logger;

    public TtsClient(HttpClient http, IDeepSeekKeyProvider keys, AiOptions options, ILogger logger)
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

    public async Task<TtsResult> TtsAsync(
        string text, string? voiceId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var trimmed = text.Trim();
        if (trimmed.Length > MaxCharacters)
        {
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Usage,
                $"TTS：文本 {trimmed.Length} 字符，超过平台上限 {MaxCharacters}");
        }

        var voice = string.IsNullOrWhiteSpace(voiceId) ? _options.TtsVoiceId : voiceId.Trim();
        var format = NormalizeFormat(_options.TtsFormat);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, _options.BaseUrl.TrimEnd('/') + TtsPath)
        {
            Content = JsonContent.Create(new
            {
                text = trimmed,
                voice_id = voice,
                format,
                // 朗读"星号井号"没有意义：让平台先剥掉 Markdown 标记
                filter_markdown = true,
            }),
        };

        using var response = await MediaRequest
            .SendAsync(_http, _keys, request, _logger, "TTS", ct)
            .ConfigureAwait(false);

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var bytes = await MediaRequest
            .ReadBytesAsync(response, _logger, "TTS", ct)
            .ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            throw new DeepSeekMediaException(DeepSeekMediaErrorKind.Parse, "TTS 返回了空音频");
        }

        if (!LooksLikeAudio(mediaType, bytes, format))
        {
            // 2xx 但内容不是音频：**绝不能落盘**。存成一个打不开的 .wav 还回「已生成」，
            // 是最坏的一种"假装成功"。
            _logger.Error(
                "TTS 期望音频，实际收到 {Type}（{Bytes} 字节，前 16 字节 {Head}）",
                mediaType, bytes.Length, Head(bytes));
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Parse, $"TTS 期望音频，实际收到 {(mediaType.Length == 0 ? "未知类型" : mediaType)}");
        }

        var path = await WriteFileAsync(bytes, format, ct).ConfigureAwait(false);

        _logger.Information(
            "TTS 完成：字符={Chars} 音色={Voice} 格式={Format} 字节={Bytes}",
            trimmed.Length, voice, format, bytes.Length);

        return new TtsResult(path, bytes.Length, format, trimmed.Length);
    }

    /// <summary>
    /// 落盘：目录不存在就建；文件名 = 时间戳 + 短随机串（同一秒两次调用也不会互相覆盖）。
    /// </summary>
    private async Task<string> WriteFileAsync(byte[] bytes, string format, CancellationToken ct)
    {
        var dir = Path.GetFullPath(_options.TtsOutputDir);
        Directory.CreateDirectory(dir);

        var stamp = BeijingTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(dir, $"tts-{stamp}-{suffix}.{format}");

        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        return path;
    }

    private static string NormalizeFormat(string? format) =>
        string.Equals(format?.Trim(), "pcm", StringComparison.OrdinalIgnoreCase) ? "pcm" : "wav";

    /// <summary>
    /// 判定「这确实是音频」——**允许清单 + 魔数**双判。
    /// </summary>
    /// <remarks>
    /// 只拦 <c>application/json</c> 是不够的：平台/网关回 <c>text/html</c>、<c>text/plain</c>
    /// 或干脆不带 Content-Type 时，错误体照样会被写成 .wav 并报成功。
    /// </remarks>
    private static bool LooksLikeAudio(string mediaType, byte[] bytes, string format)
    {
        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 类型不明（或 octet-stream）：按格式验魔数/文本特征
        if (format == "wav")
        {
            return bytes.Length >= 12
                   && bytes[0] == (byte)'R' && bytes[1] == (byte)'I'
                   && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                   && bytes[8] == (byte)'W' && bytes[9] == (byte)'A'
                   && bytes[10] == (byte)'V' && bytes[11] == (byte)'E';
        }

        // pcm 没有魔数：只要求「首字节不是文本味的 { 或 <」
        return bytes[0] != (byte)'{' && bytes[0] != (byte)'<';
    }

    /// <summary>日志用：前 16 字节的可读形态（判断是不是 JSON/HTML 错误体）。</summary>
    private static string Head(byte[] bytes) =>
        Convert.ToHexString(bytes.AsSpan(0, Math.Min(16, bytes.Length)));
}
