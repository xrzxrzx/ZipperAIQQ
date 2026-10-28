namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 一条到 sidecar 的**文本帧连接**（<c>/ws/channel</c> 的传输层抽象）。
/// </summary>
/// <remarks>
/// <para>
/// 抽出来的唯一目的：<see cref="DouyinChannel"/> 的单测不能真开 WebSocket。
/// 端点实现只管"收一帧文本 / 发一帧文本"，帧语义（<c>hello</c> / <c>inbound</c> / <c>ack</c>…）
/// 全在 <see cref="DouyinChannel"/> 里，这样协议逻辑可测、传输层薄。
/// </para>
/// <para>
/// 生命周期：一条 sidecar 连接 = 一个实例。sidecar 重连时**新建**实例并
/// <see cref="DouyinChannel.AttachAsync"/> 替换旧的，旧连接负责收尾。
/// </para>
/// </remarks>
public interface IDouyinConnection
{
    /// <summary>发送一帧文本（JSON）。失败应抛出（上层据此回填失败原因给模型）。</summary>
    Task SendAsync(string json, CancellationToken ct);

    /// <summary>
    /// 持续接收文本帧；连接关闭时正常结束。
    /// </summary>
    /// <remarks>异常应抛出，由 <see cref="DouyinChannel"/> 记为"连接断开"并等重连。</remarks>
    IAsyncEnumerable<string> ReceiveAsync(CancellationToken ct);

    /// <summary>关闭连接（幂等，可重复调用）。</summary>
    Task CloseAsync(CancellationToken ct);
}
