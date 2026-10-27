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
    /// 机器人自身在该平台的账号；未知时为 <c>null</c>。
    /// </summary>
    /// <remarks>用于过滤自激通知，以及让模型知道「哪个号是自己」。</remarks>
    string? SelfId => null;

    /// <summary>首次发现 / 变更自身账号时触发（供持久化与日志）。</summary>
    event Action<string>? SelfIdChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// 本通道覆盖的全部平台（单平台通道默认只有自己；复合通道返回全部子平台）。
    /// </summary>
    /// <remarks>
    /// 存在理由（S3-E）：<c>SelfIdRecorder</c> 这类"要为每个平台各记一份状态"的消费者，
    /// 需要一个不用猜"现在到底有哪些平台"的来源。默认实现保持加性：单平台实现零改动。
    /// </remarks>
    IReadOnlyList<string> Platforms => [Platform];

    /// <summary>
    /// 指定平台上的自身账号。单平台实现沿用 <see cref="SelfId"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 多平台实现（复合通道）必须按平台返回 —— 否则 QQ 的 selfId 会被当成抖音的，
    /// 「@ 的是不是自己」与自激过滤会同时错判。
    /// </para>
    /// <para>
    /// 写成**带默认实现的接口方法**（而不是抽象成员）是为了让它是加性变更：
    /// 现有实现（含测试里的假通道）一行都不用改。
    /// </para>
    /// </remarks>
    string? SelfIdFor(string platform) => SelfId;

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
