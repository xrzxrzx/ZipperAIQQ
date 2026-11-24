using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Server.Ai;

namespace ZipperAIQQ.Server.Ai.DeepSeekTools;

/// <summary>
/// OCR：<c>POST {base}/v1/tools/ocr</c>（multipart：<c>file</c> + <c>purpose</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 平台限制：≤100 MiB、≤1000 页、总时长 180 s、并发 15。本项目在 <see cref="AiOptions.MaxUploadBytes"/>
/// 处卡得更严（默认 20 MiB）：一次塞一本 PDF 既费钱又费内存，需要时再放宽。
/// </para>
/// <para>
/// 响应结构以实测为准（T0.3：<c>page_count</c> + <c>pages[].text</c> + <c>usage</c>），
/// 但解析是**宽容**的：字段缺失不炸，只把「拿不到」如实标成 <c>null</c>；
/// 完全解析不了才抛 <see cref="DeepSeekMediaErrorKind.Parse"/>，并把响应片段写进日志（下次好补结构）。
/// </para>
/// </remarks>
public sealed class OcrClient
{
    private const string OcrPath = "/v1/tools/ocr";

    private readonly HttpClient _http;
    private readonly IDeepSeekKeyProvider _keys;
    private readonly AiOptions _options;
    private readonly ILogger _logger;

    public OcrClient(HttpClient http, IDeepSeekKeyProvider keys, AiOptions options, ILogger logger)
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

    public async Task<OcrResult> OcrAsync(
        byte[] file, string fileName, OcrPurpose purpose, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (file.Length == 0)
        {
            throw new DeepSeekMediaException(DeepSeekMediaErrorKind.Usage, "OCR：文件是空的");
        }

        if (file.Length > _options.MaxUploadBytes)
        {
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.TooLarge,
                $"OCR：文件 {file.Length} 字节，超过上限 {_options.MaxUploadBytes}");
        }

        using var content = MediaRequest.Multipart(
            file, "file", fileName, ("purpose", ToPlatformPurpose(purpose)));

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl.TrimEnd('/') + OcrPath)
        {
            Content = content,
        };

        using var response = await MediaRequest
            .SendAsync(_http, _keys, request, _logger, "OCR", ct)
            .ConfigureAwait(false);

        var json = await MediaRequest
            .ReadStringAsync(response, _logger, "OCR", ct)
            .ConfigureAwait(false);
        var result = Parse(json);

        _logger.Information(
            "OCR 完成：文件={File} 页={Pages} 给出={Provided} token={In}/{Out}",
            fileName, result.PageCount, result.Pages.Count, result.InputTokens, result.OutputTokens);

        return result;
    }

    /// <summary>解析响应；结构对不上时抛 Parse，并把片段写进日志。</summary>
    internal OcrResult Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.Error("OCR 响应不是合法 JSON（前 500 字）：{Snippet}", Truncate(json));
            throw new DeepSeekMediaException(
                DeepSeekMediaErrorKind.Parse, "OCR 响应不是合法 JSON", inner: ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            var pages = new List<OcrPage>();
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("pages", out var pagesNode) &&
                pagesNode.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var page in pagesNode.EnumerateArray())
                {
                    index++;
                    var text = MediaRequest.StringOrNull(page, "text", "content") ?? string.Empty;
                    var reported = MediaRequest.LongOrNull(page, "index", "page_index", "page");
                    pages.Add(new OcrPage(reported is > 0 ? (int)reported.Value : index, text));
                }
            }

            var pageCount = MediaRequest.LongOrNull(root, "page_count", "pageCount", "pages_count");
            var usage = root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("usage", out var usageNode)
                ? usageNode
                : default;

            var inputTokens = MediaRequest.LongOrNull(usage, "input_tokens", "prompt_tokens", "inputTokens");
            var outputTokens = MediaRequest.LongOrNull(usage, "output_tokens", "completion_tokens", "outputTokens");

            if (pages.Count == 0 && pageCount is null)
            {
                _logger.Error("OCR 响应里既没有 pages 也没有 page_count（前 500 字）：{Snippet}", Truncate(json));
                throw new DeepSeekMediaException(
                    DeepSeekMediaErrorKind.Parse, "OCR 响应里没有 pages / page_count");
            }

            // 页数从产生处就自洽：平台回 0 或缺字段时用**逐页文本条数**兜底，
            // 否则记账会写成「用了但 0 页」（调用确实发生了、也确实计费了）。
            var reportedPages = pageCount is > 0 ? (int)pageCount.Value : pages.Count;
            return new OcrResult(reportedPages, pages, inputTokens, outputTokens);
        }
    }

    private static string ToPlatformPurpose(OcrPurpose purpose) => purpose switch
    {
        OcrPurpose.TextRecognition => "text_recognition",
        OcrPurpose.RichLayoutRecognition => "rich_layout_recognition",
        _ => "text_recognition",
    };

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500];
}
