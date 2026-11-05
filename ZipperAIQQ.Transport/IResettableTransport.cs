namespace ZipperAIQQ.Transport;

/// <summary>
/// 能「丢掉旧连接、准备重连」的传输（可选能力）。
/// </summary>
/// <remarks>
/// 为什么要单独一个接口：重连前必须先清掉 <c>Aborted</c> 的旧 socket，
/// 否则 <c>Attach</c> 见到已有 socket 会直接 return，重连永远连不上。
/// 但「是否能复位」不是所有实现都有的能力，塞进 <see cref="IWebSocketTransport"/> 会逼所有实现跟着改。
/// </remarks>
public interface IResettableTransport
{
    /// <summary>
    /// 关闭并释放当前 socket，清空接收任务与取消源，为下一次连接做准备。
    /// 可重复调用；没有连接时是空操作。
    /// </summary>
    Task ResetAsync(CancellationToken ct = default);
}
