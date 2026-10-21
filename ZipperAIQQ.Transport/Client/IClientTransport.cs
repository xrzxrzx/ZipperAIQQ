using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport.Client;

/// <summary>
/// 客户端侧 WebSocket 传输：**主动连接**远端。
/// </summary>
/// <remarks>
/// 与 <see cref="Server.IServerTransport"/> 的区别只有「谁发起连接」：
/// 服务端等别人连进来（<c>Attach</c>），客户端自己连出去（<c>ConnectAsync</c>）。
/// 连接建立之后，收发与关闭逻辑完全复用 <see cref="WebSocketDuplexBase"/>。
/// </remarks>
public interface IClientTransport : IWebSocketTransport
{
    /// <summary>收到消息时的回调。由上层（通道实现）注册。</summary>
    /// <remarks>未注册时实现方只记日志、不抛异常 —— 一帧没人处理不该把连接干崩。</remarks>
    Func<JsonNode, ValueTask>? EventHandler { get; set; }

    /// <summary>
    /// 连接到远端。
    /// </summary>
    /// <param name="uri">目标地址，如 <c>ws://127.0.0.1:17011</c>。</param>
    /// <param name="accessToken">可选鉴权 token（走 <c>Authorization: Bearer</c> 头）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>
    /// ⚠️ token 走 **Header 而不是 query string** —— query 会被代理与日志原样记下来。
    /// </remarks>
    Task ConnectAsync(Uri uri, string? accessToken = null, CancellationToken ct = default);
}
