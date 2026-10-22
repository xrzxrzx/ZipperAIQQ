using System.Globalization;
using Serilog;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZipperAIQQ.Agent.Llm;

namespace ZipperAIQQ.Server.Reply;

/// <summary>
/// 用 OpenRouter **Decisions API** 做「该不该回」判断（Jev 这类判断模型）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么单独一套：<c>typesafe/jev-1.13</c> 是 **decisions 模型**，不接受 chat/completions。
/// 它的原话：
/// <c>"is a decisions model and cannot be used with the chat/completions endpoint.
/// Use the /api/alpha/decisions endpoint instead."</c>
/// </para>
/// <para>
/// 接口形态（见 OpenRouter 文档 "Jev Tutorial"）：
/// </para>
/// <code>
/// POST {origin}/api/alpha/decisions
/// { "model": "typesafe/jev-1.13",
///   "state": { ...事实... },
///   "questions": { "should_reply": { "type": "noul",
///                                    "instructions": "...",
///                                    "criteria": { "true": "...", "false": "..." } } } }
///
/// → { "answers": { "should_reply": { "type": "noul", "noul": 0.96 } } }
/// </code>
/// <para>
/// 返回的是**概率**，不是文本 —— 所以阈值可以调（<see cref="Threshold"/>），
/// 按「误回」和「漏回」的代价取舍，而不是拍一个 0.5。
/// </para>
/// </remarks>
public sealed class DecisionsJudgeClient
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public DecisionsJudgeClient(HttpClient http, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _logger = logger;
    }

    /// <summary>判定为「该回」的概率阈值。</summary>
    /// <remarks>
    /// 0.5 是中性；调低 → 更愿意插话（漏回代价高时用），调高 → 更少出声（误回代价高时用）。
    /// 群里闲聊被误回比漏回更烦人，所以默认略偏保守。
    /// </remarks>
    public double Threshold { get; init; } = 0.55;

    /// <summary>
    /// 发一次 noul 判断，返回 (是否该回, 概率)。
    /// </summary>
    /// <param name="endpoint">judge 端点（必须是 <see cref="LlmApiKind.Decisions"/>）。</param>
    /// <param name="apiKey">明文 key（调用方从配置解密后传入，不落日志）。</param>
    /// <param name="persona">人设（让判断知道「这是个什么角色」）。</param>
    /// <param name="recent">最近的对话（一行一条，已裁剪）。</param>
    /// <param name="latest">当前这条消息。</param>
    public async Task<(bool ShouldReply, double Probability)> JudgeAsync(
        LlmEndpoint endpoint,
        string apiKey,
        string? persona,
        IReadOnlyList<string> recent,
        string latest,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var url = ResolveDecisionsUrl(endpoint.BaseUrl);

        var state = new JsonObject
        {
            ["角色设定"] = persona ?? "（未设定）",
            ["最近的对话"] = recent.Count > 0 ? string.Join('\n', recent) : "（无）",
            ["当前消息"] = latest,
        };

        var body = new JsonObject
        {
            ["model"] = endpoint.Model,
            ["state"] = state,
            ["questions"] = new JsonObject
            {
                ["should_reply"] = new JsonObject
                {
                    ["type"] = "noul",
                    ["instructions"] = "这条『当前消息』需要我（角色设定里那个人）现在开口回复吗？",
                    ["criteria"] = new JsonObject
                    {
                        ["true"] = "这条消息是在对我说话、在问我、在接我的话、或者明显期待我回应（比如提问、点名、抛话头）。",
                        ["false"] = "这条消息是在跟别人说话、自言自语、刷屏、纯表情、明显不需要我插话的群聊闲聊。",
                    },
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"decisions API {(int)response.StatusCode}：{Truncate(text, 300)}");
        }

        var probability = ParseNoul(text);
        var shouldReply = probability >= Threshold;

        _logger.Debug(
            "Decisions 判断：概率 {P:F3}（阈值 {T:F2}）→ {R}",
            probability, Threshold, shouldReply ? "回" : "不回");

        return (shouldReply, probability);
    }

    /// <summary>把 chat 用的 baseUrl 换成 decisions 的地址。</summary>
    /// <remarks>
    /// 用户在客户端填的是 <c>https://openrouter.ai/api/v1</c>（chat 用），
    /// 而 decisions 在 <c>https://openrouter.ai/api/alpha/decisions</c>。
    /// 所以**从 origin 重新拼**，不猜路径。
    /// </remarks>
    public static Uri ResolveDecisionsUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"judge 端点的 baseUrl 不合法：{baseUrl}");
        }

        return new Uri($"{uri.Scheme}://{uri.Authority}/api/alpha/decisions");
    }

    /// <summary>从响应里取 <c>answers.&lt;question&gt;.noul</c>。</summary>
    private static double ParseNoul(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("answers", out var answers) ||
            !answers.TryGetProperty("should_reply", out var answer) ||
            !answer.TryGetProperty("noul", out var noul) ||
            !noul.TryGetDouble(out var probability))
        {
            throw new InvalidOperationException($"decisions 响应里没有 answers.should_reply.noul：{Truncate(json, 300)}");
        }

        return Math.Clamp(probability, 0d, 1d);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
