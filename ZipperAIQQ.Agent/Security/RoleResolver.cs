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
    private readonly HashSet<string> _admins;
    private readonly HashSet<string> _toolWhitelist;

    public ConfiguredRoleResolver(IEnumerable<string> admins, IEnumerable<string> toolWhitelist)
    {
        ArgumentNullException.ThrowIfNull(admins);
        ArgumentNullException.ThrowIfNull(toolWhitelist);

        _admins = admins.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        _toolWhitelist = toolWhitelist.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
    }

    public Role Resolve(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Role.Normal;
        }

        if (_admins.Contains(userId))
        {
            return Role.Admin;
        }

        return _toolWhitelist.Contains(userId) ? Role.Whitelist : Role.Normal;
    }
}
