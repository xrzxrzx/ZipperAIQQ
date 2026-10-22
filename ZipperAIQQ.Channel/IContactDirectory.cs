using System.Text.Json.Nodes;

namespace ZipperAIQQ.Channel;

/// <summary>平台联系人（好友）信息。</summary>
/// <param name="UserId">账号。</param>
/// <param name="Nickname">昵称。</param>
/// <param name="Remark">备注名（可能为空）。</param>
public sealed record ContactInfo(string UserId, string Nickname, string? Remark);

/// <summary>群信息。</summary>
public sealed record GroupInfo(
    string GroupId,
    string Name,
    int MemberCount,
    int MaxMemberCount);

/// <summary>群成员信息（含群头衔）。</summary>
/// <param name="UserId">账号。</param>
/// <param name="Nickname">QQ 昵称。</param>
/// <param name="Card">群名片（群昵称）。</param>
/// <param name="Role">角色：owner / admin / member。</param>
/// <param name="Title">群头衔（自定义头衔，可能为空）。</param>
public sealed record GroupMemberInfo(
    string UserId,
    string Nickname,
    string? Card,
    string Role,
    string? Title);

/// <summary>
/// 平台目录查询：好友 / 群 / 群成员。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不直接在 <see cref="IMessageChannel"/> 上暴露 `call_api(action, params)`：
/// 那等于把平台概念漏到上层，违反架构接缝 B（「上层不得直接依赖任何平台类型」）。
/// </para>
/// <para>
/// 这里只暴露**平台无关的业务能力**；是不是 OneBot、是不是 NapCat，上层不需要知道。
/// </para>
/// </remarks>
public interface IContactDirectory
{
    /// <summary>好友列表（用于「选择主人」这类配置）。</summary>
    Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default);

    /// <summary>群信息；查询失败返回 <c>null</c>。</summary>
    Task<GroupInfo?> GetGroupAsync(string groupId, CancellationToken ct = default);

    /// <summary>群成员列表（含角色与头衔）；查询失败返回空列表。</summary>
    Task<IReadOnlyList<GroupMemberInfo>> GetGroupMembersAsync(string groupId, CancellationToken ct = default);

    /// <summary>
    /// 直接调一个平台 API（给「AI 自主查询」用）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是**受控逃生门**：只有白名单里的 action 允许通过（见实现里的 ActionAllowlist），
    /// 不能让它变成一个"什么都能调"的口子。
    /// </remarks>
    Task<JsonObject?> InvokeApiAsync(string action, JsonObject? parameters, CancellationToken ct = default);
}
