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

    /// <summary>
    /// 群列表（用于名单候选 / 解析「主人所在的群」）。
    /// </summary>
    /// <remarks>
    /// 与好友列表同口径：查询失败**抛异常**，不能返回空列表冒充「一个群都没有」。
    /// </remarks>
    Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken ct = default);

    // ────────────────────────────────────────────────────────────
    //  写操作（**必须经权限门禁**，不能走通用的 InvokeApiAsync）
    // ────────────────────────────────────────────────────────────
    //  为什么单独列方法而不是加进 ActionAllowlist：
    //  那个白名单是"模型可以自主调用"的只读集合，一旦把写操作塞进去，
    //  等于给了模型一把「不经过门禁就能改我账号」的钥匙。

    /// <summary>
    /// 改自己的个性签名。
    /// </summary>
    /// <param name="text">签名内容（平台可能截断过长的）。</param>
    Task SetSignatureAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// 戳一戳某人。
    /// </summary>
    /// <param name="userId">目标账号。</param>
    /// <param name="groupId">群号（群内戳需要；私聊留空）。</param>
    Task SendPokeAsync(string userId, string? groupId, CancellationToken ct = default);

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
    Task<JsonNode?> InvokeApiAsync(string action, JsonObject? parameters, CancellationToken ct = default);
}
