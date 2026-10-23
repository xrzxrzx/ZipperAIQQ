using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Search;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Server.Search;

/// <summary>
/// DeepSeek 原生 Search / Fetch（需求 D33）的适配器。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 上游 Search API 的**确切调用形态尚未实测**（D33 只定了"用 DeepSeek 原生能力"）。
/// 这里按"POST {base}/search {query, top_k} + Bearer key"实现，并把解析写成宽容模式：
/// 兼容 <c>data.results</c> / <c>results</c> / 顶层数组。实测定型后只需改这一个文件。
/// </para>
/// <para>
/// key 复用 LLM 端点（primary）的加密 key；base 可用 <c>ZIPPER_SEARCH_BASEURL</c> 覆盖。
/// 失败一律返回"不可用"，**绝不**让搜索把对话卡住。
/// </para>
/// </remarks>
public sealed class DeepSeekWebSearchProvider : IWebSearchProvider
{
    private readonly HttpClient _http;
    private readonly IConfigStore _configs;
    private readonly ISecretProtector _protector;
    private readonly string _baseUrl;
    private readonly ILogger _logger;

    public DeepSeekWebSearchProvider(
        HttpClient http,
        IConfigStore configs,
        ISecretProtector protector,
        string? baseUrl,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _configs = configs;
        _protector = protector;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? "https://api.deepseek.com" : baseUrl).TrimEnd('/');
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int topK, CancellationToken ct = default)
    {
        var key = await GetKeyAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            return [];
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/search")
        {
            Content = JsonContent.Create(new { query, top_k = topK }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.Warning("网页搜索失败：HTTP {Status}", (int)response.StatusCode);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var array = FindArray(doc.RootElement);
        if (array is null)
        {
            return [];
        }

        var results = new List<WebSearchResult>();
        foreach (var item in array.Value.EnumerateArray())
        {
            var title = GetString(item, "title") ?? GetString(item, "name") ?? string.Empty;
            var url = GetString(item, "url") ?? GetString(item, "link") ?? string.Empty;
            var snippet = GetString(item, "snippet") ?? GetString(item, "content") ?? GetString(item, "summary") ?? string.Empty;

            if (url.Length > 0)
            {
                results.Add(new WebSearchResult(title, url, snippet));
            }
        }

        return results;
    }

    public async Task<string?> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 极简正文提取：去脚本/样式/标签；够模型读即可，不引入 HTML 解析依赖
        var text = System.Text.RegularExpressions.Regex.Replace(
            html,
            "<(script|style)[^>]*>.*?</\\1>",
            " ",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\s{2,}", " ").Trim();

        return text.Length == 0 ? null : text;
    }

    private async Task<string?> GetKeyAsync(CancellationToken ct)
    {
        var endpoint = await _configs.GetEndpointAsync("primary", ct).ConfigureAwait(false);
        return endpoint is null ? null : _protector.Unprotect(endpoint.KeyCipher);
    }

    private static JsonElement? FindArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        foreach (var name in new[] { "results", "data", "items" })
        {
            if (root.TryGetProperty(name, out var node))
            {
                if (node.ValueKind == JsonValueKind.Array)
                {
                    return node;
                }

                if (node.ValueKind == JsonValueKind.Object &&
                    node.TryGetProperty("results", out var inner) &&
                    inner.ValueKind == JsonValueKind.Array)
                {
                    return inner;
                }
            }
        }

        return null;
    }

    private static string? GetString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
