namespace ZipperAIQQ.Agent.Search;

/// <summary>一条网页搜索结果。</summary>
public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>
/// 网页搜索 / 抓取（需求 D33，问题记录 #38）。
/// </summary>
/// <remarks>
/// 接口放 Agent、实现放 Server：Agent 拿不到明文 key，
/// 由 Server 解出后注入（依赖方向仍是 Server → Agent）。
/// </remarks>
public interface IWebSearchProvider
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int topK,
        CancellationToken ct = default);

    Task<string?> FetchAsync(string url, CancellationToken ct = default);
}
