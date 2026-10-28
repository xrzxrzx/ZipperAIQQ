using System.ComponentModel;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 抖音专用工具（决策五 / S3-E 的 E8）：查询抖音侧的好友与群。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不复用 <c>query_napcat_api</c>：那是 OneBot/NapCat 的 action 门面，抖音根本不认这些 action。
/// 两个平台的数据源也不同（NapCat 进程内 vs sidecar 的 <c>query.contacts</c>）。
/// </para>
/// <para>
/// 标注为 <see cref="ToolPlatform.Douyin"/>：QQ 会话里**连 schema 都不给模型看**
/// （否则它会给 QQ 用户列一堆抖音好友，或者更糟——调了失败还宣称成功）。
/// </para>
/// <para>
/// 目录由 <see cref="IPlatformContacts"/> 按平台取：抖音没接入时取不到目录，
/// 这组工具**不会出现**（不是出现后报错）。
/// </para>
/// </remarks>
public static class DouyinTools
{
    /// <summary>列出抖音好友。</summary>
    public const string FriendsToolName = "douyin_friends";

    /// <summary>列出抖音群。</summary>
    public const string GroupsToolName = "douyin_groups";

    /// <summary>一次最多返回多少条（避免把整份好友列表灌进上下文）。</summary>
    private const int MaxItems = 30;

    public static IEnumerable<AITool> Create(
        IPlatformContacts contacts,
        ToolCallContext context,
        IExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        // 只在抖音会话里挂（平台过滤也会挡一道，这里是"别白造对象"的第二道）
        if (!string.Equals(context.Chat.Platform, ToolPlatforms.DouyinId, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var directory = contacts.For(ToolPlatforms.DouyinId);
        if (directory is null)
        {
            // 抖音还没接入：工具不出现，而不是出现了再报错
            yield break;
        }

        yield return AIFunctionFactory.Create(
            async (CancellationToken ct) =>
            {
                if (!IsEnabled(policy, FriendsToolName, context.Role, out var denial))
                {
                    return denial;
                }

                try
                {
                    var friends = await directory.GetFriendsAsync(ct).ConfigureAwait(false);
                    if (friends.Count == 0)
                    {
                        return "抖音好友列表是空的（或者 sidecar 暂时取不到）。";
                    }

                    var lines = friends
                        .Take(MaxItems)
                        .Select(f => $"- {f.Nickname}（{f.UserId}）{(string.IsNullOrWhiteSpace(f.Remark) ? string.Empty : $"备注：{f.Remark}")}");

                    var suffix = friends.Count > MaxItems ? $"\n…共 {friends.Count} 个，只列前 {MaxItems} 个。" : string.Empty;
                    return "抖音好友：\n" + string.Join('\n', lines) + suffix;
                }
                catch (Exception ex)
                {
                    // 失败要响：让模型知道"没查到"，而不是编一份名单
                    return $"查询抖音好友失败：{ex.Message}";
                }
            },
            new AIFunctionFactoryOptions
            {
                Name = FriendsToolName,
                Description = "列出当前账号的抖音好友（昵称 + uid），用于回答「我在抖音有哪些好友」这类问题。",
            });

        yield return AIFunctionFactory.Create(
            async (CancellationToken ct) =>
            {
                if (!IsEnabled(policy, GroupsToolName, context.Role, out var denial))
                {
                    return denial;
                }

                try
                {
                    var groups = await directory.GetGroupsAsync(ct).ConfigureAwait(false);
                    if (groups.Count == 0)
                    {
                        return "抖音群列表是空的（或者 sidecar 暂时取不到）。";
                    }

                    var lines = groups
                        .Take(MaxItems)
                        .Select(g => $"- {g.Name}（群 {g.GroupId}，{g.MemberCount} 人）");

                    var suffix = groups.Count > MaxItems ? $"\n…共 {groups.Count} 个，只列前 {MaxItems} 个。" : string.Empty;
                    return "抖音群：\n" + string.Join('\n', lines) + suffix;
                }
                catch (Exception ex)
                {
                    return $"查询抖音群失败：{ex.Message}";
                }
            },
            new AIFunctionFactoryOptions
            {
                Name = GroupsToolName,
                Description = "列出当前账号所在的抖音群（群名 + 群号 + 人数）。",
            });
    }

    /// <summary>策略开关 + 角色门禁（与其它工具同一套口径）。</summary>
    private static bool IsEnabled(
        IExecutionPolicy policy, string toolName, Role role, out string denial)
    {
        if (!policy.IsEnabled(PolicyCategory.Tool, toolName))
        {
            denial = $"工具「{toolName}」已在客户端被禁用。";
            return false;
        }

        var required = policy.MinRole(PolicyCategory.Tool, toolName, Role.Normal);
        if (role < required)
        {
            denial = $"权限不足：工具「{toolName}」需要 {required} 及以上角色，当前是 {role}。";
            return false;
        }

        denial = string.Empty;
        return true;
    }
}
