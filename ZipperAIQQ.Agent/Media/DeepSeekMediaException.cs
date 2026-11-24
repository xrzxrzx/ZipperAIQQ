namespace ZipperAIQQ.Agent.Media;

/// <summary>
/// 全家桶媒体接口的失败分类（**给模型看的那一层**要能说出"为什么没做到"）。
/// </summary>
public enum DeepSeekMediaErrorKind
{
    /// <summary>能力没开 / 没配 key。</summary>
    Unavailable,

    /// <summary>鉴权失败（401 / 403）。</summary>
    Auth,

    /// <summary>请求本身不合法（4xx：字段缺失、格式不支持、文件损坏）。</summary>
    Usage,

    /// <summary>文件过大（413 / 超出本项目的上传上限）。</summary>
    TooLarge,

    /// <summary>限流（429）。</summary>
    RateLimited,

    /// <summary>超时 / 网络不可达。</summary>
    Timeout,

    /// <summary>上游 5xx。</summary>
    Upstream,

    /// <summary>响应结构对不上（字段缺失 / 不是合法 JSON）。</summary>
    Parse,
}

/// <summary>
/// 媒体接口失败。
/// </summary>
/// <remarks>
/// 与 <c>WebSearchException</c> 同构：**带状态码**，让上层能决定是重试、降级还是直接如实告诉用户。
/// </remarks>
public sealed class DeepSeekMediaException : Exception
{
    public DeepSeekMediaException(
        DeepSeekMediaErrorKind kind, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public DeepSeekMediaErrorKind Kind { get; }

    public int? StatusCode { get; }

    /// <summary>给模型看的一句话（不暴露内网细节，也不编造成功）。</summary>
    public string ToModelMessage() => Kind switch
    {
        DeepSeekMediaErrorKind.Unavailable => "这个能力当前不可用（服务端未开启或没配 key）。如实告诉对方没做成。",
        DeepSeekMediaErrorKind.Auth => "调用凭据不对（鉴权失败）。如实告诉对方没做成，别提内部原因。",
        DeepSeekMediaErrorKind.Usage => $"请求被拒：{Message}",
        DeepSeekMediaErrorKind.TooLarge => $"文件太大，平台不收：{Message}",
        DeepSeekMediaErrorKind.RateLimited => "被限流了，稍后再试。如实告诉对方这次没做成。",
        DeepSeekMediaErrorKind.Timeout => "调用超时了。如实告诉对方这次没做成。",
        DeepSeekMediaErrorKind.Upstream => "上游服务出错了。如实告诉对方这次没做成。",
        _ => $"结果解析失败：{Message}。如实告诉对方这次没做成。",
    };
}
