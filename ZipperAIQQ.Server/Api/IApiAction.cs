using System.Text.Json;

namespace ZipperAIQQ.Server.Api;

/// <summary>一个 HTTP action 的处理器。</summary>
public interface IApiAction
{
    /// <summary>action 名（与请求里的 <c>action</c> **完全一致**，大小写敏感）。</summary>
    string Name { get; }

    /// <summary>
    /// 处理请求。
    /// </summary>
    /// <remarks>
    /// 抛异常会被 dispatcher 统一转成 <c>500</c>（回给客户端的是简短原因，堆栈只进日志）。
    /// </remarks>
    Task<object?> HandleAsync(JsonElement payload, CancellationToken ct);
}
