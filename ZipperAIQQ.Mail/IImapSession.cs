using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Mail;

/// <summary>
/// 一封从 IMAP 拉下来、**尚未落库**的邮件（已解码成纯文本）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么先抽出 <c>ImapMessage</c> 而不是让会话直接产出 <see cref="ZipperAIQQ.Channel.Models.IncomingMail"/>：
/// <c>IncomingMail</c> 的形状由通道层定（Chat / EventId / OccurredAt），IMAP 只关心
/// 「UID + 头 + 正文」。中间隔一层，单测才能用假会话喂数据，
/// 不必碰真实 IMAP（编码规范 §13「外部依赖用接口 + 假实现」）。
/// </para>
/// <para>
/// 正文**不做截断**：截断规则归 <see cref="MailSourceOptions.MaxBodyChars"/>，
/// 由 <see cref="MailAccountSession"/> 统一执行 —— 否则每个会话实现都要各自记得截，
/// 漏一个就会把整封 HTML 转出来的长文塞进事件流。
/// </para>
/// </remarks>
/// <param name="Uid">IMAP UID（同步水位键，单调递增）。</param>
/// <param name="MessageId">RFC <c>Message-Id</c>；服务器没给时为 <see langword="null"/>（少见但存在）。</param>
/// <param name="Subject">已解码的主题（可能为空串）。</param>
/// <param name="FromAddress">发件人地址。</param>
/// <param name="FromName">发件人显示名（可能为空）。</param>
/// <param name="BodyText">纯文本正文；没有纯文本部分时由 HTML 剥离而来，可能为空串。</param>
/// <param name="HasAttachments">是否带附件（本期**不解析附件内容**）。</param>
/// <param name="SentAt">邮件 <c>Date</c> 头（取平台时间，不取本地时钟）。</param>
public sealed record ImapMessage(
    long Uid,
    string? MessageId,
    string Subject,
    string FromAddress,
    string? FromName,
    string BodyText,
    bool HasAttachments,
    DateTimeOffset SentAt);

/// <summary>
/// 一个 IMAP 连接会话（**每账号一个**，内部串行操作）。
/// </summary>
/// <remarks>
/// <para>
/// 抽成接口的唯一目的是可测：MailKit 的 <c>ImapClient</c> 不保证线程安全，
/// 也不该在单测里出现。真实实现在 <see cref="MailKitImapSession"/>。
/// </para>
/// <para>
/// ⚠️ **串行契约**：调用方必须保证同一个会话上的调用不并发
/// （<see cref="MailAccountSession"/> 用单账号一个信号量保证）。
/// </para>
/// </remarks>
public interface IImapSession : IAsyncDisposable
{
    /// <summary>
    /// 连接并认证（163 / 126 / yeah.net 还要在认证前补发 IMAP <c>ID</c> 命令）。
    /// </summary>
    /// <param name="account">账号配置（主机 / 端口 / SSL / 登录名）。</param>
    /// <param name="credential">**明文**凭据：只在本方法内使用，绝不落库、绝不进日志。</param>
    /// <param name="ct">取消令牌。</param>
    Task ConnectAsync(MailAccount account, string credential, CancellationToken ct);

    /// <summary>
    /// 取服务器当前的**最大 UID**（邮箱为空返回 0）。
    /// </summary>
    /// <remarks>首次同步拿它把水位一次对齐，避免空邮箱时每个轮询周期都重走首次同步。</remarks>
    Task<long> GetMaxUidAsync(CancellationToken ct);

    /// <summary>
    /// 取 UID 大于 <paramref name="afterUid"/> 的邮件，**按 UID 升序**。
    /// </summary>
    /// <param name="afterUid">
    /// 水位。**传 0 表示首次同步**：实现应返回「最新的 <paramref name="limit"/> 封」而不是最旧的
    /// （否则首次同步会从十年前的邮件开始扫）。
    /// </param>
    /// <param name="limit">本次最多返回多少封（分页）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<IReadOnlyList<ImapMessage>> FetchSinceAsync(long afterUid, int limit, CancellationToken ct);

    /// <summary>断开连接（幂等：未连接时直接返回）。</summary>
    Task DisconnectAsync(CancellationToken ct);
}

/// <summary>
/// 会话工厂：**每次调用返回新会话**（连接不共享）。
/// </summary>
/// <remarks>线程安全由「每账号一个会话 + 串行操作」保证，不靠会话本身。</remarks>
public interface IImapSessionFactory
{
    /// <summary>新建一个未连接的会话。</summary>
    IImapSession Create();
}
