namespace ZipperAIQQ.Agent.Security;

/// <summary>
/// 权限门禁实现：能力 → 最低角色，越界一律拒绝（deny-by-default）。
/// </summary>
public sealed class PermissionGate : IPermissionGate
{
    /// <summary>
    /// **不可配置的硬底线**（FR-6.6）：高危能力不允许被放宽到这条线以下。
    /// </summary>
    /// <remarks>
    /// 删文件没有 undo → 永远只给管理员；写文件至少白名单。
    /// 「可配置」不等于「可以配成危险」—— 一次手滑就是沙箱失守。
    /// </remarks>
    private static readonly IReadOnlyDictionary<ToolCapability, Role> HardFloor =
        new Dictionary<ToolCapability, Role>
        {
            [ToolCapability.ReadFile] = Role.Normal,
            [ToolCapability.WriteFile] = Role.Whitelist,
            [ToolCapability.DeleteFile] = Role.Admin,
            // 定时任务：默认仅管理员，但**允许**配置给白名单（需求 14.6 明确要求可显式开启）
            [ToolCapability.Schedule] = Role.Whitelist,
            [ToolCapability.CrossSessionHistory] = Role.Admin,
        };

    /// <summary>默认最低角色（M1 的「预置默认允许集」，FR-6.7）。</summary>
    private static readonly IReadOnlyDictionary<ToolCapability, Role> Defaults =
        new Dictionary<ToolCapability, Role>
        {
            // 读**不默认全开**：跨会话读取必须管理员（FR-7.4），本会话读取给白名单
            [ToolCapability.ReadFile] = Role.Whitelist,
            [ToolCapability.WriteFile] = Role.Whitelist,
            [ToolCapability.DeleteFile] = Role.Admin,
            // 需求 14.6：默认仅管理员；白名单需要显式开启（这里改这一行即可）
            [ToolCapability.Schedule] = Role.Admin,
            [ToolCapability.CrossSessionHistory] = Role.Admin,
        };

    private readonly IReadOnlyDictionary<ToolCapability, Role> _minRoles;

    public PermissionGate()
        : this(Defaults)
    {
    }

    public PermissionGate(IReadOnlyDictionary<ToolCapability, Role> minRoles)
    {
        ArgumentNullException.ThrowIfNull(minRoles);
        ValidateHardFloor(minRoles);
        _minRoles = new Dictionary<ToolCapability, Role>(minRoles);
    }

    public PermissionDecision Check(ToolCapability capability, ToolCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var required = MinRoleOf(capability);

        return context.Role >= required
            ? PermissionDecision.Allow()
            : PermissionDecision.Deny(
                $"权限不足：{Describe(capability)}需要 {required} 及以上角色，当前是 {context.Role}。");
    }

    public bool IsAllowed(ToolCapability capability, Role role) => role >= MinRoleOf(capability);

    /// <summary>
    /// 校验配置没有突破硬底线（FR-6.6）。**启动时**调用，越界直接拒绝启动。
    /// </summary>
    public static void ValidateHardFloor(IReadOnlyDictionary<ToolCapability, Role> minRoles)
    {
        ArgumentNullException.ThrowIfNull(minRoles);

        foreach (var (capability, floor) in HardFloor)
        {
            if (!minRoles.TryGetValue(capability, out var configured))
            {
                throw new InvalidOperationException($"权限配置缺少能力 {capability} 的最低角色。");
            }

            if (configured < floor)
            {
                throw new InvalidOperationException(
                    $"权限配置越界：{capability} 被配置为 {configured}，硬底线是 {floor}。" +
                    "高危能力不允许放宽给更低角色。");
            }
        }
    }

    private Role MinRoleOf(ToolCapability capability) =>
        _minRoles.TryGetValue(capability, out var value) ? value : Role.Admin;

    private static string Describe(ToolCapability capability) => capability switch
    {
        ToolCapability.ReadFile => "读文件",
        ToolCapability.WriteFile => "写文件",
        ToolCapability.DeleteFile => "删除文件",
        ToolCapability.Schedule => "定时任务",
        _ => capability.ToString(),
    };
}
