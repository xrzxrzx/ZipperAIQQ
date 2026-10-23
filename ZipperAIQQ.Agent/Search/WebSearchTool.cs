using System.ComponentModel;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Search;

namespace ZipperAIQQ.Agent.Search;

/// <summary>给模型的网页搜索 / 抓取工具（#38）。</summary>
public static class WebSearchTool
{
    public const string SearchToolName = "web_search";
    public const string FetchToolName = "fetch_url";

    public static IEnumerable<AITool> Create(IWebSearchProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        yield return AIFunctionFactory.Create(
            async (string query, int? topK, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    return "请给出要搜索的关键词。";
                }

                if (!provider.IsAvailable)
                {
                    return "搜索服务当前不可用（服务端未配置或连接失败）。";
                }

                try
                {
                    var results = await provider
                        .SearchAsync(query.Trim(), Math.Clamp(topK ?? 5, 1, 10), ct)
                        .ConfigureAwait(false);

                    if (results.Count == 0)
                    {
                        return $"没搜到「{query}」的结果。";
                    }

                    return string.Join('\n', results.Select((r, i) =>
                        $"{i + 1}. {r.Title}\n   {r.Url}\n   {r.Snippet}"));
                }
                catch (Exception ex)
                {
                    return $"搜索失败：{ex.Message}（如实告诉对方没查成，不要编造）。";
                }
            },
            name: SearchToolName,
            description:
                "搜索网页获取实时信息（新闻、价格、资料等）。\n" +
                "参数：query（关键词）、topK（默认 5）。返回标题 / URL / 摘要。\n" +
                "需要引用来源时把 URL 带上。");

        yield return AIFunctionFactory.Create(
            async (string url, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return "请给出要抓取的 URL。";
                }

                if (!provider.IsAvailable)
                {
                    return "抓取服务当前不可用。";
                }

                try
                {
                    var text = await provider.FetchAsync(url.Trim(), ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return "这个页面没有抓到正文（可能被反爬或不是 HTML）。";
                    }

                    return text.Length > 8000 ? text[..8000] + "\n…（截断）" : text;
                }
                catch (Exception ex)
                {
                    return $"抓取失败：{ex.Message}（如实告诉对方没查成）。";
                }
            },
            name: FetchToolName,
            description: "抓取一个网页并返回纯文本（最多 8000 字）。参数：url。");
    }
}
