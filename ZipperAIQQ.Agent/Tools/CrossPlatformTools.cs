using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 跨平台通讯录（I-2 / R-08）：一次查**所有已接入平台**的好友 / 群，结果带平台标签。
/// </summary>
/// <remarks>
/// <para>
/// 和 <see cref="DouyinTools"/> 的分工：那组是「当前平台专用」，本工具是「主人问『我都有哪些群』」时用的
/// 汇总视图 —— 平台标签（<c>[qq]</c> / <c>[douyin]</c>）让模型能区分同名的人/群。
/// </para>
/// <para>
/// 每个平台单独 try：一个平台查失败只标注该平台失败，不影响其它平台返回。
/// 未接入的平台直接说明「未接入」，而不是编造空列表。
/// </para>
/// <para>
/// 权限：<see cref="ToolCapability.QueryContacts"/>，默认管理员（好友列表能反推社交关系）。
/// </para>
/// </remarks>
public static class CrossPlatformTools
{
    public const string ToolName = "query_contacts";

    /// <summary>每个平台最多列多少条（避免把整份通讯录灌进上下文）。</summary>
    private const int MaxItemsPerPlatform = 20;

    public static IEnumerable<AITool> Create(
        IPlatformContacts contacts,
        ToolCallContext context,
        IExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        yield return AIFunctionFactory.Create(
            async (string kind, string? platform, string? keyword, CancellationToken ct) =>
            {
                if (!IsEnabled(policy, context.Role, out var denial))
                {
                    return denial;
                }

                var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
                if (normalized is not ("friends" or "groups"))
                {
                    return "参数 kind 只支持 friends（好友）或 groups（群）。";
                }

                var targets = string.IsNullOrWhiteSpace(platform)
                    ? contacts.Platforms
                    : contacts.Platforms
                        .Where(p => string.Equals(p, platform.Trim(), StringComparison.OrdinalIgnoreCase))
                        .ToList();

                if (targets.Count == 0)
                {
                    return $"没有名为 {platform} 的已接入平台；当前已接入：{string.Join('、', contacts.Platforms)}。";
                }

                var blocks = new List<string>();

                foreach (var target in targets)
                {
                    var directory = contacts.For(target);
                    if (directory is null)
                    {
                        blocks.Add($"[{target}] 未接入，取不到通讯录。");
                        continue;
                    }

                    try
                    {
                        blocks.Add(normalized == "friends"
                            ? await DescribeFriendsAsync(target, directory, keyword, ct).ConfigureAwait(false)
                            : await DescribeGroupsAsync(target, directory, keyword, ct).ConfigureAwait(false));
                    }
                    catch (Exception ex)
                    {
                        blocks.Add($"[{target}] 查询失败：{ex.Message}");
                    }
                }

                return string.Join("\n\n", blocks);
            },
            new AIFunctionFactoryOptions
            {
                Name = ToolName,
                Description = "跨平台查询好友或群：kind=friends|groups；platform 可选（不填查所有已接入平台）；keyword 可选（按名称过滤）。",
            });
    }

    private static async Task<string> DescribeFriendsAsync(
        string platform,
        IContactDirectory directory,
        string? keyword,
        CancellationToken ct)
    {
        var friends = await directory.GetFriendsAsync(ct).ConfigureAwait(false);
        var matched = Filter(friends, keyword, f => f.Remark ?? f.Nickname);

        if (matched.Count == 0)
        {
            return $"[{platform}] 没有匹配的好友。";
        }

        var lines = matched
            .Take(MaxItemsPerPlatform)
            .Select(f => $"- {f.Remark ?? f.Nickname}（{platform}:p{f.UserId}）");

        var suffix = matched.Count > MaxItemsPerPlatform
            ? $"\n…共 {matched.Count} 个，只列前 {MaxItemsPerPlatform} 个。"
            : string.Empty;

        return $"[{platform}] 好友：\n" + string.Join('\n', lines) + suffix;
    }

    private static async Task<string> DescribeGroupsAsync(
        string platform,
        IContactDirectory directory,
        string? keyword,
        CancellationToken ct)
    {
        var groups = await directory.GetGroupsAsync(ct).ConfigureAwait(false);
        var matched = Filter(groups, keyword, g => g.Name);

        if (matched.Count == 0)
        {
            return $"[{platform}] 没有匹配的群。";
        }

        var lines = matched
            .Take(MaxItemsPerPlatform)
            .Select(g => $"- {g.Name}（{platform}:g{g.GroupId}，{g.MemberCount} 人）");

        var suffix = matched.Count > MaxItemsPerPlatform
            ? $"\n…共 {matched.Count} 个，只列前 {MaxItemsPerPlatform} 个。"
            : string.Empty;

        return $"[{platform}] 群：\n" + string.Join('\n', lines) + suffix;
    }

    private static List<T> Filter<T>(IReadOnlyList<T> items, string? keyword, Func<T, string> name)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return items.ToList();
        }

        var needle = keyword.Trim();
        return items
            .Where(item => name(item).Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>策略开关 + 角色门禁（与其它工具同一套口径）。</summary>
    private static bool IsEnabled(IExecutionPolicy policy, Role role, out string denial)
    {
        if (!policy.IsEnabled(PolicyCategory.Tool, ToolName))
        {
            denial = $"工具「{ToolName}」已在客户端被禁用。";
            return false;
        }

        var capability = ToolCatalog.CapabilityOf(ToolName);
        var fallback = capability is null ? Role.Normal : PermissionGate.DefaultRoleOf(capability.Value);
        var required = policy.MinRole(PolicyCategory.Tool, ToolName, fallback);

        if (role < required)
        {
            denial = $"权限不足：工具「{ToolName}」需要 {required} 及以上角色，当前是 {role}。";
            return false;
        }

        denial = string.Empty;
        return true;
    }
}
