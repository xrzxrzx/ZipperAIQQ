namespace ZipperAIQQ.Storage.Owners;

/// <summary>
/// 主人跨平台账号的**只读镜像**（D-05 底座）。
/// </summary>
/// <remarks>
/// 现在权限判定仍以 <c>access.{platform}.owner</c> 为准；这张表是为了让
/// 「出事了要通知主人」这类跨平台动作能一次问出「主人都在哪些平台」而不必逐平台试。
/// 迁移方向是单向的：settings → 表。等 R-08 真正统一身份时再反过来。
/// </remarks>
public interface IOwnerAccountStore
{
    Task<IReadOnlyList<OwnerAccount>> ListAsync(CancellationToken ct = default);

    /// <summary>按平台批量补写（已存在的跳过）；返回新增条数。</summary>
    Task<int> UpsertAsync(IEnumerable<OwnerAccount> accounts, CancellationToken ct = default);
}
