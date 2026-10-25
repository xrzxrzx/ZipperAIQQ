using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Serilog;
using ZipperAIQQ.Agent.Media;

namespace ZipperAIQQ.Server.Media;

/// <summary>
/// 下载图片字节并识别真实 MIME；带一个短期内存缓存。
/// </summary>
/// <remarks>
/// <para>
/// QQ 图片 URL 带 <c>rkey</c>，**几分钟到几十分钟后就会 400**（实测见
/// <c>docs/06-调研/QQ图片链接实测.md</c>）。所以必须在链接还有效时下载，
/// 不能把 URL 直接交给模型（<c>UriContent</c>）让上游再取。
/// </para>
/// <para>
/// 缓存按 <c>fileId</c>（没有则 URL）索引，TTL 30 分钟、总量上限 64MB；
/// 只放内存，不落盘（避免临时链接文件与清理问题）。
/// </para>
/// </remarks>
public sealed class HttpImageFetcher : IImageFetcher
{
    private const long MaxBytes = 8 * 1024 * 1024;
    private const long MaxCacheBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    private sealed record CacheEntry(FetchedImage Image, DateTimeOffset At);

    public HttpImageFetcher(HttpClient http, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _logger = logger;
    }

    public async Task<FetchedImage?> FetchAsync(
        string url, string? fileId, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var key = string.IsNullOrWhiteSpace(fileId) ? url : fileId;
        if (_cache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < Ttl)
        {
            return cached.Image;
        }

        try
        {
            using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning("图片下载失败：HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                _logger.Warning("图片超过上限（{Size} 字节），跳过", response.Content.Headers.ContentLength);
                return null;
            }

            var bytes = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            var mime = ImageProbe.DetectMime(bytes)
                       ?? response.Content.Headers.ContentType?.MediaType
                       ?? "application/octet-stream";

            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("下载内容不是图片：{Mime}", mime);
                return null;
            }

            var image = new FetchedImage(bytes, mime);
            Remember(key, image);

            _logger.Information(
                "图片已下载：mime={Mime} bytes={Bytes} fileId={FileId}",
                mime, bytes.Length, key);
            return image;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "图片下载异常：{FileId}", key);
            return null;
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return [];
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private void Remember(string key, FetchedImage image)
    {
        _cache[key] = new CacheEntry(image, DateTimeOffset.UtcNow);

        var total = _cache.Values.Sum(x => (long)x.Image.Bytes.Length);
        if (total <= MaxCacheBytes)
        {
            return;
        }

        foreach (var old in _cache.OrderBy(x => x.Value.At).Take(_cache.Count / 2))
        {
            _cache.TryRemove(old.Key, out _);
        }
    }
}
