using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Storage.Mails;

/// <summary>
/// 邮件仓储：账号、邮件、摘要三层（邮件接入设计 §3.4 + 实施微调 M4）。
/// </summary>
/// <remarks>
/// <para>
/// 邮件**不进 <c>Messages</c> 表**：那是对话流，检索 / 摘要 / 向量化都按会话组织，
/// 混进邮件会让「AI 记得聊过什么」这条链路被通知污染。邮件单独三张表。
/// </para>
/// <para>
/// 去重靠 <c>(AccountId, MessageId)</c> 唯一索引 + 写前预检：
/// 断线重连与重启补拉都会重复投递同一封邮件。
/// </para>
/// </remarks>
public interface IMailRepository
{
    /// <summary>
    /// 追加一封邮件；返回新行主键，**已存在则返回 <c>null</c>**（幂等，不抛异常）。
    /// </summary>
    /// <remarks>
    /// 为什么要返回主键而不是 <c>bool</c>：下游摘要 / 汇总都要按 <c>MailId</c> 写状态，
    /// 再反查一次既多一次往返、也在 <c>MessageId</c> 为空时无法定位到行。
    /// </remarks>
    Task<long?> AppendAsync(IncomingMail mail, CancellationToken ct = default);

    /// <summary>该账号下是否已有这条 <c>Message-Id</c>。</summary>
    Task<bool> ExistsAsync(string accountId, string messageId, CancellationToken ct = default);

    /// <summary>列出邮件（新的在前）。<paramref name="accountId"/> 为空 = 全部账号。</summary>
    Task<IReadOnlyList<StoredMail>> ListAsync(string? accountId, int limit, CancellationToken ct = default);

    /// <summary>按主键取一封（含摘要）。</summary>
    Task<StoredMail?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>取**还没摘要**的邮件（旧的在先），供摘要服务补做。</summary>
    Task<IReadOnlyList<StoredMail>> GetPendingSummaryAsync(int limit, CancellationToken ct = default);

    /// <summary>写入 / 覆盖一条摘要（<paramref name="mailId"/> 不存在则抛 <see cref="InvalidOperationException"/>）。</summary>
    Task SaveSummaryAsync(
        long mailId,
        string summary,
        bool needsAction,
        string status,
        string? error,
        CancellationToken ct = default);

    /// <summary>取**摘要成功但还没转发**的邮件（旧的在先），供汇总服务组批。</summary>
    Task<IReadOnlyList<StoredMail>> GetPendingDigestAsync(int limit, CancellationToken ct = default);

    /// <summary>标记这些邮件"已转发"。<paramref name="forwardedAt"/> 缺省取当前时间。</summary>
    Task MarkForwardedAsync(
        IReadOnlyList<long> mailIds,
        DateTimeOffset? forwardedAt = null,
        CancellationToken ct = default);

    /// <summary>全部账号（含禁用的：管理页要能看到并重新启用）。</summary>
    Task<IReadOnlyList<MailAccount>> LoadAccountsAsync(CancellationToken ct = default);

    /// <summary>新增或整行覆盖一个账号（按 <see cref="MailAccount.Id"/>）。</summary>
    Task UpsertAccountAsync(MailAccount account, CancellationToken ct = default);

    /// <summary>删除账号**及其邮件与摘要**（同一事务，不留孤儿行）。</summary>
    Task DeleteAccountAsync(string id, CancellationToken ct = default);

    /// <summary>只更新同步水位与状态（高频写，避免整行覆盖把用户刚改的配置冲掉）。</summary>
    Task UpdateSyncStateAsync(
        string accountId,
        long lastUid,
        DateTimeOffset? lastSyncAt,
        string? lastError,
        CancellationToken ct = default);
}
