using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using ZipperAIQQ.Agent.Search;

namespace ZipperAIQQ.Server.Search;

/// <summary>
/// DeepSeek 原生 Search / Fetch（需求 D33）的适配器。
/// </summary>
/// <remarks>
/// <para>
/// 官方端点（2026-10-25 实测，见 <c>docs/06-调研/DeepSeek搜索实测.md</c>）：
/// <c>POST {base}/v1/tools/search</c>（<c>count</c> / <c>need_content</c>）与
/// <c>POST {base}/v1/tools/fetch</c>（<c>url</c> → <c>content</c> Markdown）。
/// </para>
/// <para>
/// 失败语义：非 2xx / 鉴权失败 / 解析失败 → 抛 <see cref="WebSearchException"/>，
/// **不再**伪装成「没搜到」；只有 200 且结果为空才是真的没搜到。
/// </para>
/// <para>
/// fetch 采用「官方优先 + 自建回落」：官方有反爬与正文提取，但按成功请求计费；
/// 官方失败时才用旧的 GET + 正则剥标签，兼顾可用性与成本。
/// </para>
/// </remarks>
public sealed class DeepSeekWebSearchProvider : IWebSearchProvider
{
    private const string SearchPath = "/v1/tools/search";
    private const string FetchPath = "/v1/tools/fetch";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string?>> _keyProvider;
    private readonly Func<SearchSnapshot> _settings;
    private readonly ILogger _logger;

    public DeepSeekWebSearchProvider(
        HttpClient http,
        Func<CancellationToken, Task<string?>> keyProvider,
        Func<SearchSnapshot> settings,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(keyProvider);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _keyProvider = keyProvider;
        _settings = settings;
        _logger = logger;
    }

    public bool IsAvailable => _settings().Enabled;

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int topK, CancellationToken ct = default)
    {
        if (!_settings().Enabled)
        {
            throw new WebSearchException("网页搜索已被配置关闭");
        }

        var key = await _keyProvider(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new WebSearchException("没有可用的搜索 key（LLM 端点未配置）");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings().BaseUrl}{SearchPath}")
        {
            Content = JsonContent.Create(new
            {
                query,
                count = Math.Clamp(topK, 1, 50),
                need_content = false,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadBodySnippetAsync(response, ct).ConfigureAwait(false);
            _logger.Warning(
                "网页搜索失败：HTTP {Status} {Detail}",
                (int)response.StatusCode,
                detail);

            throw new WebSearchException(
                $"上游返回 HTTP {(int)response.StatusCode}",
                (int)response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            throw new WebSearchException("搜索响应里没有 results 数组");
        }

        var parsed = new List<WebSearchResult>();
        foreach (var item in results.EnumerateArray())
        {
            var title = GetString(item, "title") ?? string.Empty;
            var url = GetString(item, "url") ?? string.Empty;
            var snippet = GetString(item, "snippet")
                          ?? GetString(item, "summary")
                          ?? GetString(item, "content")
                          ?? string.Empty;

            if (url.Length > 0)
            {
                parsed.Add(new WebSearchResult(title, url, snippet));
            }
        }

        _logger.Information("网页搜索完成：query={Query} 结果={Count}", query, parsed.Count);
        return parsed;
    }

    public async Task<string?> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new WebSearchException($"不是合法的 http/https 地址：{url}");
        }

        if (_settings().Enabled)
        {
            var official = await TryOfficialFetchAsync(uri, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(official))
            {
                return official;
            }
        }

        return await SelfFetchAsync(uri, ct).ConfigureAwait(false);
    }

    private async Task<string?> TryOfficialFetchAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            var key = await _keyProvider(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings().BaseUrl}{FetchPath}")
            {
                Content = JsonContent.Create(new { url = uri.ToString() }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning("官方 fetch 失败：HTTP {Status}，回落自建抓取", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
            {
                _logger.Warning("官方 fetch 响应没有 content，回落自建抓取");
                return null;
            }

            var text = content.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "官方 fetch 异常，回落自建抓取");
            return null;
        }
    }

    /// <summary>自建抓取：GET 页面 + 正则剥标签（免费，但站点一拦就抓不到）。</summary>
    private async Task<string?> SelfFetchAsync(Uri uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new WebSearchException(
                $"抓取页面失败：HTTP {(int)response.StatusCode}",
                (int)response.StatusCode);
        }

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var text = Regex.Replace(
            html,
            "<(script|style)[^>]*>.*?</\\1>",
            " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "\\s{2,}", " ").Trim();

        return text.Length == 0 ? null : text;
    }

    private static async Task<string> ReadBodySnippetAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length <= 200 ? body : body[..200];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? GetString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
