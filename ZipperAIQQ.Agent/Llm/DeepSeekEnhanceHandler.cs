using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ZipperAIQQ.Agent.Llm;

/// <summary>
/// 往对话请求体里注入 DeepSeek「对话增强」参数（联网搜索 / 阅读链接 / 内容安全 / 会话标题）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么走 <see cref="DelegatingHandler"/> 而不是 SDK 选项：这四个参数是**服务端扩展字段**
/// （<c>tools:[{type:"deepseek_web_search"}]</c>、<c>safety_check</c>、<c>title</c>），
/// MEAI / OpenAI SDK 的强类型选项里没有它们的映射位。改出站 JSON 是唯一不动 SDK 的做法，
/// 而且能在**编译产物**上单测断言（把 handler 换成假的都能验）。
/// </para>
/// <para>
/// 注入是**合并**而不是覆盖：<c>tools</c> 数组里可能已经有我们自己的函数工具，
/// 只追加缺失的那几个 <c>type</c>，绝不把已有工具数组替换掉 —— 否则一轮对话直接失去全部工具能力。
/// </para>
/// <para>
/// 只处理对话路径（<c>/responses</c> 与 <c>/chat/completions</c>）：别把请求体改写规则
/// 漏到 <c>/files</c>、<c>/models</c> 这些接口上。
/// </para>
/// </remarks>
public sealed class DeepSeekEnhanceHandler : DelegatingHandler
{
    private const string WebSearchToolType = "deepseek_web_search";
    private const string ReadUrlToolType = "deepseek_read_url";

    private readonly DeepSeekEnhanceOptions _options;
    private readonly Serilog.ILogger? _logger;

    public DeepSeekEnhanceHandler(DeepSeekEnhanceOptions options, Serilog.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.AnyEnabled &&
            request.Content is not null &&
            IsChatRequest(request.RequestUri))
        {
            var original = await request.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            var rewritten = Inject(original, _options);
            if (rewritten is not null)
            {
                var contentType = request.Content.Headers.ContentType
                                  ?? new MediaTypeHeaderValue("application/json");
                request.Content = new StringContent(rewritten, Encoding.UTF8);
                request.Content.Headers.ContentType = contentType;
            }
            else if (_options.AnyEnabled)
            {
                // Inject 返回 null 只有两种可能：body 空 / 不是能解析的 JSON。
                // 原样放行是对的（不能打断一次正常对话），但必须留痕 —— 否则"开关没生效"永远查不出来。
                _logger?.Debug(
                    "对话增强：请求体无法解析，已原样放行（路径={Path}，长度={Length}）",
                    request.RequestUri?.AbsolutePath, original.Length);
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只有这两个路径吃增强参数（<c>/responses</c> 与 <c>/chat/completions</c>）。</summary>
    public static bool IsChatRequest(Uri? uri)
    {
        if (uri is null)
        {
            return false;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 纯函数：把 JSON 请求体改写成带增强字段的版本；解析不了就返回 <c>null</c>（**原样放行**）。
    /// </summary>
    /// <remarks>
    /// 解析失败必须放行而不是抛：请求体不是我们的，抛异常等于把一次正常对话打断在传输层。
    /// </remarks>
    public static string? Inject(string body, DeepSeekEnhanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.AnyEnabled || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (Exception)
        {
            return null;
        }

        if (node is not JsonObject root)
        {
            return null;
        }

        MergeServerTools(root, options);

        if (options.SafetyCheckHigh)
        {
            root["safety_check"] = new JsonObject { ["level"] = "high" };
        }

        if (options.Title)
        {
            root["title"] = new JsonObject { ["enabled"] = true };
        }

        return root.ToJsonString();
    }

    /// <summary>把服务端工具**追加**进已有 tools 数组（已有同 type 就跳过）。</summary>
    private static void MergeServerTools(JsonObject root, DeepSeekEnhanceOptions options)
    {
        var wanted = new List<string>(2);
        if (options.WebSearch)
        {
            wanted.Add(WebSearchToolType);
        }

        if (options.ReadUrl)
        {
            wanted.Add(ReadUrlToolType);
        }

        if (wanted.Count == 0)
        {
            return;
        }

        if (root["tools"] is not JsonArray tools)
        {
            tools = [];
            root["tools"] = tools;
        }

        foreach (var type in wanted)
        {
            var exists = tools.Any(t =>
                t is JsonObject o &&
                o["type"] is JsonValue v &&
                string.Equals(v.ToString(), type, StringComparison.Ordinal));

            if (!exists)
            {
                tools.Add(new JsonObject { ["type"] = type });
            }
        }
    }
}
