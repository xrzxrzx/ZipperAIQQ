namespace ZipperAIQQ.Agent.Security;

/// <summary>按发送者 ID 解析角色。</summary>
public interface IRoleResolver
{
    /// <summary>解析角色；不在任何名单里就是普通成员（deny-by-default）。</summary>
    Role Resolve(string? userId);
}

/// <summary>
/// 配置驱动的角色解析：管理员 &gt; 工具白名单 &gt; 普通。
/// </summary>
/// <remarks>
/// ⚠️ 这里的「白名单」是**工具权限**白名单，与消息过滤用的
/// <c>ServerOptions.Whitelist</c>（ZIPPER_WHITELIST）**不是一回事** ——
/// 一个是「谁能让它动手」，一个是「它愿意理谁」。
/// </remarks>
public sealed class ConfiguredRoleResolver : IRoleResolver
{
    private readonly HashSet<string> _admins;   // 注意：启动后可被 SetAdmins 整体替换（见下）
    private readonly HashSet<string> _toolWhitelist;

    /// <summary>主人（第 17 项）：启动时读库、运行时由 API 改写。</summary>
    private volatile string? _owner;

    /// <summary>运行时更新的管理员集合（null 表示仍用启动时的那份）。</summary>
    private volatile HashSet<string>? _adminsOverride;

    public ConfiguredRoleResolver(IEnumerable<string> admins, IEnumerable<string> toolWhitelist)
    {
        ArgumentNullException.ThrowIfNull(admins);
        ArgumentNullException.ThrowIfNull(toolWhitelist);

        _admins = admins.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        _toolWhitelist = toolWhitelist.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>设置主人（<c>null</c> 表示没设）。</summary>
    public void SetOwner(string? userId) =>
        _owner = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();

    /// <summary>当前主人（没设为 <c>null</c>）。</summary>
    public string? Owner => _owner;

    /// <summary>运行时替换管理员名单（客户端改配置后立即生效）。</summary>
    public void SetAdmins(IEnumerable<string> admins)
    {
        ArgumentNullException.ThrowIfNull(admins);
        _adminsOverride = admins
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>当前管理员（含启动时的）。</summary>
    public IReadOnlyCollection<string> Admins => _adminsOverride ?? _admins;

    public Role Resolve(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Role.Normal;
        }

        // 主人优先于一切（含管理员名单）
        if (_owner is { Length: > 0 } owner && string.Equals(userId, owner, StringComparison.Ordinal))
        {
            return Role.Owner;
        }

        if ((_adminsOverride ?? _admins).Contains(userId))
        {
            return Role.Admin;
        }

        return _toolWhitelist.Contains(userId) ? Role.Whitelist : Role.Normal;
    }
}
