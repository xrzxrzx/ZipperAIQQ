namespace ZipperAIQQ.Agent.Security;

/// <summary>按发送者 ID 解析角色。</summary>
/// <remarks>
/// <para>
/// ⚠️ S3-C（决策四）起**必须带平台**：主人与管理员都是"某个平台上的某个人"，
/// QQ 号与抖音 uid 完全无关 —— 拿 QQ 的管理员表去判抖音用户，等于谁都不是管理员（或更糟：误判）。
/// </para>
/// <para>
/// 故意**不保留** <c>Resolve(userId)</c> 的单参重载：那会变成"平台忘了传就默认 qq"的静默陷阱，
/// 而这正是多平台权限最容易出事的地方。让编译器把每个调用点都逼出来改一遍。
/// </para>
/// </remarks>
public interface IRoleResolver
{
    /// <summary>解析角色；不在该平台的任何名单里就是普通成员（deny-by-default）。</summary>
    /// <param name="userId">发送者在平台内的账号。</param>
    /// <param name="platform">平台标识（<c>qq</c> / <c>douyin</c> …）。</param>
    Role Resolve(string? userId, string platform);
}

/// <summary>
/// 配置驱动的角色解析：主人 &gt; 管理员 &gt; 工具白名单 &gt; 普通，**每个平台一套名单**。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ 这里的「工具白名单」是**工具权限**白名单，与消息过滤用的
/// <see cref="ZipperAIQQ.Channel.Filtering.ChatFilterOptions.Whitelist"/>（黑白名单）**不是一回事** ——
/// 一个是「谁能让它动手」，一个是「它愿意理谁」。
/// </para>
/// <para>
/// 工具白名单**不按平台分**（决策四只要求主人 / 管理员 / 黑白名单平台化）：
/// 它是"这台机器允许谁动文件"的运维口径，与"哪个平台的会话"无关。
/// </para>
/// </remarks>
public sealed class ConfiguredRoleResolver : IRoleResolver
{
    /// <summary>单平台时代的默认平台（启动配置与环境变量只喂它）。</summary>
    public const string DefaultPlatform = "qq";

    private readonly object _gate = new();

    /// <summary>平台 → 名单（主人 + 管理员）。不可变快照，读路径直接取引用。</summary>
    private readonly Dictionary<string, PlatformScope> _scopes = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _toolWhitelist;

    public ConfiguredRoleResolver(
        IEnumerable<string> admins,
        IEnumerable<string> toolWhitelist,
        string platform = DefaultPlatform)
    {
        ArgumentNullException.ThrowIfNull(admins);
        ArgumentNullException.ThrowIfNull(toolWhitelist);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        _toolWhitelist = toolWhitelist
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        _scopes[platform.Trim()] = new PlatformScope(
            Owner: null,
            Admins: admins.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>已配置的平台（顺序不保证）。</summary>
    public IReadOnlyCollection<string> Platforms
    {
        get
        {
            lock (_gate)
            {
                return _scopes.Keys.ToList();
            }
        }
    }

    /// <summary>设置某平台的主人（<c>null</c> 表示没设）。</summary>
    public void SetOwner(string platform, string? userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var key = platform.Trim();
        var owner = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();

        lock (_gate)
        {
            var current = _scopes.TryGetValue(key, out var scope) ? scope : PlatformScope.Empty;
            _scopes[key] = current with { Owner = owner };
        }
    }

    /// <summary>当前某平台的主人（没设为 <c>null</c>）。</summary>
    public string? GetOwner(string platform)
    {
        lock (_gate)
        {
            return _scopes.TryGetValue(platform.Trim(), out var scope) ? scope.Owner : null;
        }
    }

    /// <summary>运行时替换某平台的管理员名单（客户端改配置后立即生效）。</summary>
    public void SetAdmins(string platform, IEnumerable<string> admins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentNullException.ThrowIfNull(admins);

        var key = platform.Trim();
        var next = admins.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);

        lock (_gate)
        {
            var current = _scopes.TryGetValue(key, out var scope) ? scope : PlatformScope.Empty;
            _scopes[key] = current with { Admins = next };
        }
    }

    /// <summary>某平台当前的管理员。</summary>
    public IReadOnlyCollection<string> Admins(string platform)
    {
        lock (_gate)
        {
            return _scopes.TryGetValue(platform.Trim(), out var scope)
                ? scope.Admins.ToList()
                : [];
        }
    }

    public Role Resolve(string? userId, string platform)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(platform))
        {
            return Role.Normal;
        }

        PlatformScope? scope;
        lock (_gate)
        {
            _scopes.TryGetValue(platform.Trim(), out scope);
        }

        // 主人优先于一切（含管理员名单）
        if (scope?.Owner is { Length: > 0 } owner && string.Equals(userId, owner, StringComparison.Ordinal))
        {
            return Role.Owner;
        }

        if (scope is not null && scope.Admins.Contains(userId))
        {
            return Role.Admin;
        }

        return _toolWhitelist.Contains(userId) ? Role.Whitelist : Role.Normal;
    }

    /// <summary>一个平台的名单快照。</summary>
    private sealed record PlatformScope(string? Owner, HashSet<string> Admins)
    {
        public static readonly PlatformScope Empty = new(null, []);
    }
}
