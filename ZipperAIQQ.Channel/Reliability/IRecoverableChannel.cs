namespace ZipperAIQQ.Channel.Reliability;

/// <summary>
/// 能「自己恢复」的通道（可选能力，不污染 <see cref="IMessageChannel"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么单独一个接口而不是塞进 <see cref="IMessageChannel"/>：<see cref="IMessageChannel"/>
/// 是接缝 B 的契约，加成员会逼所有实现（含测试假通道）跟着改；而「怎么恢复」本来就是平台私事。
/// </para>
/// <para>
/// 各平台的恢复动作：
/// </para>
/// <list type="bullet">
///   <item>QQ：重拨 NapCat 的 WS（需要先丢掉已 Aborted 的旧 socket）；</item>
///   <item>抖音：服务端没有出站连接，恢复动作是**空**（等 sidecar 反向回连），
///         只要返回 <c>IsConnected</c> 的结果即可；</item>
///   <item>新平台：按自己的连接形态实现。</item>
/// </list>
/// <para>
/// **不要**在这里做退避等待 —— 退避是 <see cref="ReconnectPolicy"/> 的事，Supervisor 统一调。
/// </para>
/// </remarks>
public interface IRecoverableChannel
{
    /// <summary>平台标识（与 <see cref="IMessageChannel.Platform"/> 一致）。</summary>
    string Platform { get; }

    /// <summary>当前是否已连接（用于 Supervisor 判断要不要恢复）。</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 尝试恢复一次。
    /// </summary>
    /// <returns><c>true</c> = 已恢复（或本来就通）；<c>false</c> = 本次没救回来，等下一轮退避。</returns>
    /// <remarks>实现方**不应**吞掉异常当失败 —— 该抛的抛，由 Supervisor 记录并继续退避。</remarks>
    Task<bool> TryRecoverAsync(CancellationToken ct);
}
