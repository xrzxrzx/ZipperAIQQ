using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Server.Ai;

namespace ZipperAIQQ.Server.Ai.DeepSeekTools;

/// <summary>
/// 三个媒体接口**共用**的请求外壳：带 key、发请求、把失败翻译成可读分类。
/// </summary>
/// <remarks>
/// <para>
/// 为什么单独抽出来：OCR / TTS / ASR 的错误语义必须**一致** ——
/// 三个客户端各写一份状态码表，迟早出现「413 在 OCR 叫太大、在 ASR 叫未知」这种裂口。
/// </para>
/// <para>
/// 失败一律**抛**而不是返回空结果：上层要能如实告诉模型「没做到」，
/// 吞成空字符串会让它以为「图里没有文字」「音频里没人说话」。
/// </para>
/// </remarks>
internal static class MediaRequest
{
    /// <summary>留在日志与异常里的响应片段上限（够定位问题，又不会把整段正文写进日志）。</summary>
    private const int SnippetLimit = 500;

    /// <summary>
    /// 带上全局 key 发送请求；非 2xx 时读片段、分类并抛 <see cref="DeepSeekMediaException"/>。
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        IDeepSeekKeyProvider keys,
        HttpRequestMessage request,
        ILogger logger,
        string what,
        CancellationToken ct)
    {
        var key = await keys.GetAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Unavailable, $"{what}：没有可用的 API key");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (ex.InnerException is TimeoutException || !ct.IsCancellationRequested)
        {
            // HttpClient.Timeout 触发时抛 TaskCanceledException，**.NET 5+ 的 InnerException 是 TimeoutException**；
            // 外部 token 被取消则不是。两个判据并用，避免"超时与取消同时发生"时判错。
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Timeout, $"{what}：调用超时", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Upstream, $"{what}：网络不可达（{ex.Message}）", inner: ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        var snippet = await SnippetAsync(response, ct).ConfigureAwait(false);
        response.Dispose();

        logger.Warning("{What} 失败：HTTP {Status} {Snippet}", what, status, snippet);
        throw Classify(status, snippet, what);
    }

    /// <summary>状态码 + 平台文案 → 分类（**顺序有意义**：先认特殊码，再按段归类）。</summary>
    public static DeepSeekMediaException Classify(int status, string? snippet, string what)
    {
        var kind = status switch
        {
            401 or 403 => DeepSeekMediaErrorKind.Auth,
            413 => DeepSeekMediaErrorKind.TooLarge,
            429 => DeepSeekMediaErrorKind.RateLimited,
            408 or 504 => DeepSeekMediaErrorKind.Timeout,
            >= 500 => DeepSeekMediaErrorKind.Upstream,
            >= 400 => DeepSeekMediaErrorKind.Usage,
            _ => DeepSeekMediaErrorKind.Upstream,
        };

        var detail = string.IsNullOrWhiteSpace(snippet) ? string.Empty : $"：{snippet}";
        return new DeepSeekMediaException(
            kind, $"{what}失败（HTTP {status}）{detail}", status);
    }

    /// <summary>读响应体前 <see cref="SnippetLimit"/> 个字符（读不出来就算了，别掩盖原始错误）。</summary>
    public static async Task<string> SnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length <= SnippetLimit ? body : body[..SnippetLimit];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 读字符串响应体，并把「读体阶段」的超时/网络错误也翻成同一套分类。
    /// </summary>
    /// <remarks>
    /// <see cref="SendAsync"/> 只包住了「发请求」那一步；响应体是**流式**读的，
    /// 读一半超时抛的同样是 <see cref="TaskCanceledException"/>，不在这里收就会以
    /// 「操作已取消」这种没法解释给用户的话逃到工具层。
    /// </remarks>
    public static async Task<string> ReadStringAsync(
        HttpResponseMessage response, ILogger logger, string what, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.Warning("{What} 读取响应超时", what);
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Timeout, $"{what}：读取响应超时", inner: ex);
        }
        catch (IOException ex)
        {
            logger.Warning(ex, "{What} 读取响应失败", what);
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Upstream, $"{what}：读取响应失败（{ex.Message}）", inner: ex);
        }
    }

    /// <summary>读二进制响应体（音频），错误分类与 <see cref="ReadStringAsync"/> 一致。</summary>
    public static async Task<byte[]> ReadBytesAsync(
        HttpResponseMessage response, ILogger logger, string what, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.Warning("{What} 读取响应超时", what);
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Timeout, $"{what}：读取响应超时", inner: ex);
        }
        catch (IOException ex)
        {
            logger.Warning(ex, "{What} 读取响应失败", what);
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Upstream, $"{what}：读取响应失败（{ex.Message}）", inner: ex);
        }
    }

    /// <summary>把「文件」塞进 multipart（场名 + 文件名 + 可选附加字段）。</summary>
    public static MultipartFormDataContent Multipart(
        byte[] bytes, string field, string fileName, params (string Name, string Value)[] extra)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(GuessMime(fileName));
        content.Add(file, field, fileName);

        foreach (var (name, value) in extra)
        {
            content.Add(new StringContent(value), name);
        }

        return content;
    }

    /// <summary>按扩展名猜 MIME（平台按 Content-Type 判格式，猜错会被 4xx 拒）。</summary>
    public static string GuessMime(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".wav" => "audio/wav",
            ".ogg" or ".opus" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            _ => "application/octet-stream",
        };

    /// <summary>把一个数字字段读成 <c>long?</c>（缺失 / 非数字都算「没拿到」，不编 0）。</summary>
    public static long? LongOrNull(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>按顺位读第一个存在的字符串字段。</summary>
    public static string? StringOrNull(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
