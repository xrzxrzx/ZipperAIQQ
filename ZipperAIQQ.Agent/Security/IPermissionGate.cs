using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Agent.Security;

/// <summary>
/// 一次工具调用需要的上下文（**由平台注入，模型无法伪造**）。
/// </summary>
public sealed record ToolCallContext
{
    public required ChatRef Chat { get; init; }
    public required SenderInfo Sender { get; init; }
    public required Role Role { get; init; }
}

/// <summary>权限判定结果。</summary>
/// <param name="Allowed">是否放行。</param>
/// <param name="Reason">拒绝原因（会**回填给模型**，见 FR-6.4）。</param>
public sealed record PermissionDecision(bool Allowed, string? Reason)
{
    public static PermissionDecision Allow() => new(true, null);

    public static PermissionDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// 接缝 C：工具执行前的权限门禁（FR-6）。
/// </summary>
/// <remarks>
/// 拒绝时**返回说明而不是抛异常** —— 让模型知道「你调不了」，从而换个方式回答（FR-6.4）。
/// 抛异常只会让它以为工具坏了，然后反复重试。
/// </remarks>
public interface IPermissionGate
{
    /// <summary>按能力 + 上下文判定。</summary>
    PermissionDecision Check(ToolCapability capability, ToolCallContext context);

    /// <summary>该角色能不能用这个能力（用于「连工具都不给模型看」）。</summary>
    bool IsAllowed(ToolCapability capability, Role role);
}
