namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 一条到 sidecar 的**传输**：在 <see cref="IDouyinConnection"/> 之上补「这条连接从哪来」。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要单独一层：抖底座有两条 sidecar（<c>douyin-sidecar</c> 的 <c>im</c>、
/// <c>douyin-web-sidecar</c> 的 <c>web</c>），它们**共用同一套帧协议**，唯一要区别的是
/// 「谁连进来的」—— 用于 <c>douyin.driver</c> 闸门、日志、状态页。
/// 帧语义（<c>hello</c> / <c>inbound</c> / <c>ack</c>…）仍在 <see cref="DouyinChannel"/>，传输层**薄**。
/// </para>
/// <para>
/// 今天只有一个实现：<c>WsDouyinTransport</c>（<c>/ws/channel</c> 的 WebSocket）。
/// 留接口是为了回到「进程内传输」时通道代码一行不用改（实施方案 §G1）。
/// </para>
/// </remarks>
public interface IDouyinTransport : IDouyinConnection
{
    /// <summary>传输形态（今天是 <c>ws</c>；进程内会是别的值）。</summary>
    string Kind { get; }

    /// <summary>对端描述（日志 / 状态页用；不含任何凭据）。</summary>
    string Peer { get; }
}
