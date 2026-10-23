namespace ZipperAIQQ.Agent.Security;

/// <summary>可配置策略的三类对象（问题记录 #28）。</summary>
public enum PolicyCategory
{
    Tool,
    Command,
    ChannelApi,
}

/// <summary>策略目录中的一项（代码里的默认值 + 中文名）。</summary>
public sealed record PolicyItem(
    PolicyCategory Category,
    string Key,
    string Label,
    Role DefaultRole,
    bool DefaultEnabled = true,
    Role? HardFloor = null);

/// <summary>
/// 工具 / 命令 / 平台 API 的运行时开关与最低角色。
/// </summary>
/// <remarks>
/// 存储走 <c>RuntimeSettings</c> KV：
/// <c>policy.enabled:&lt;category&gt;:&lt;key&gt;</c> 与 <c>policy.role:&lt;category&gt;:&lt;key&gt;</c>。
/// 未配置时用 <see cref="PolicyItem"/> 里的代码默认值 —— 升级后行为不变。
/// </remarks>
public interface IExecutionPolicy
{
    /// <summary>目录（三类合并，供客户端展示）。</summary>
    IReadOnlyList<PolicyItem> Catalog { get; }

    /// <summary>是否启用；没配置时返回 <paramref name="fallback"/>。</summary>
    bool IsEnabled(PolicyCategory category, string key, bool fallback = true);

    /// <summary>最低角色；没配置时返回 <paramref name="fallback"/>。</summary>
    Role MinRole(PolicyCategory category, string key, Role fallback);

    /// <summary>重新从库里加载快照。</summary>
    Task ReloadAsync(CancellationToken ct = default);
}
