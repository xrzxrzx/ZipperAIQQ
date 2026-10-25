namespace ZipperAIQQ.Agent.Search;

/// <summary>网页搜索 / 抓取失败（与「搜到 0 条」严格区分）。</summary>
/// <remarks>
/// 旧实现把 404 当成空结果返回，模型只能看到「没搜到」——用户与模型都不知道搜索坏了。
/// 现在非 2xx、鉴权失败、响应无法解析都抛这个异常，工具层据此如实报「搜索失败」。
/// </remarks>
public sealed class WebSearchException : Exception
{
    public WebSearchException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP 状态码；解析失败等非 HTTP 错误为 null。</summary>
    public int? StatusCode { get; }
}
