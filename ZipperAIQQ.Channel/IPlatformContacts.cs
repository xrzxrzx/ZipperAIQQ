namespace ZipperAIQQ.Channel;

/// <summary>
/// 按平台取联系人目录（S3-E / 决策四第 6 条）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么放在 Channel：Agent 层要用它（抖音专用工具按平台取目录），但 Server 的注册表实现
/// 不能反向依赖 Agent。放在契约层，Server 实现、Agent 消费，双方都只认平台名。
/// </para>
/// <para>
/// 取不到返回 <c>null</c>：新平台还没接入时，工具应当**不出现**，而不是调用时才炸。
/// </para>
/// </remarks>
public interface IPlatformContacts
{
    /// <summary>已注册的平台。</summary>
    IReadOnlyList<string> Platforms { get; }

    /// <summary>取某平台的目录；未注册返回 <c>null</c>。</summary>
    IContactDirectory? For(string platform);
}
