using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Channel;

/// <summary>
/// 平台无关的「信息源」：**只有入站，没有出站**。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="IMessageChannel"/> 的区别：邮件这类源只收不发，
/// 硬套 <see cref="IMessageChannel"/> 会得到一个永远抛异常的 <c>SendAsync</c>，
/// 等于把「这个平台不能发」这件事伪装成一个没实现的接口。
/// </para>
/// <para>
/// 拆开还有一个好处：邮件**不进对话链路**（它是通知流），
/// 消费方可以是独立的管道，不必挤进「收消息 → 落库 → 回话」那条路径。
/// </para>
/// </remarks>
public interface IMessageSource
{
    /// <summary>平台标识，如 <c>mail</c>。与 <see cref="ChatRef.Platform"/> 对应。</summary>
    string Platform { get; }

    /// <summary>持续接收入站事件；实现方负责断线与重连。</summary>
    /// <remarks>正常取消时以 <see cref="OperationCanceledException"/> 结束，与 <see cref="IMessageChannel.ReceiveAsync"/> 同口径。</remarks>
    IAsyncEnumerable<InboundEvent> ReceiveAsync(CancellationToken ct);
}
