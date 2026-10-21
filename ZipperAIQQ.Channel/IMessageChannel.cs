using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Channel;

/// <summary>
/// 平台无关的消息通道（接缝 B 的落点）。
/// </summary>
/// <remarks>
/// <para>
/// 上层（Agent / Server / 工具 / 权限）**只认这个接口**，不得直接依赖任何平台类型。
/// </para>
/// <para>
/// 验收方式（FR-1.1）：在上层项目的源码里 <c>grep</c> 不到
/// <c>NapCat</c> / <c>OneBot</c> / <c>QQ</c> 这些平台专有名词。
/// </para>
/// </remarks>
public interface IMessageChannel
{
    /// <summary>平台标识，如 <c>qq</c>。与 <see cref="ChatRef.Platform"/> 对应。</summary>
    string Platform { get; }

    /// <summary>
    /// 建立连接（若通道需要）。已连接的实现应幂等。
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>本通道支持的消息能力，用于动态生成工具参数描述。</summary>
    ChannelCapabilities Capabilities { get; }

    /// <summary>
    /// 持续接收**入站事件**（消息与通知）。
    /// </summary>
    /// <remarks>
    /// 实现方应在内部处理断线重连；正常取消时以 <see cref="OperationCanceledException"/> 结束。
    /// 返回基类 <see cref="InboundEvent"/> 而非 <see cref="IncomingMessage"/> ——
    /// 通知与消息共用同一条管道（FR-18.1）。
    /// </remarks>
    IAsyncEnumerable<InboundEvent> ReceiveAsync(CancellationToken ct);

    /// <summary>
    /// 发送一条消息。
    /// </summary>
    /// <remarks>
    /// 失败时应抛出，由上层转成「回填给模型的失败原因」（FR-2.3），
    /// 不要静默吞掉 —— 吞掉会让模型以为发出去了。
    /// </remarks>
    Task SendAsync(OutgoingMessage message, CancellationToken ct);
}
