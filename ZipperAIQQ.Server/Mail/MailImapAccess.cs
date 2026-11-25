using Serilog;
using ZipperAIQQ.Mail;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Server.Mail;

/// <summary>
/// 按需读 IMAP：临时开一条连接，取一封邮件的 HTML / 原文，取完即断。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不复用收信会话（<see cref="MailAccountSession"/>）：那个会话的公开面只有
/// 「跑循环 / 同步一轮」，它内部的水位与退避状态是为轮询设计的；把「取某一封」塞进去
/// 等于给一条长跑循环加一个外来入口，锁与状态都会跟着变复杂。
/// 这里**另开一条连接**、用完就断：IMAP 本身允许多连接（163 上限约 10 个），
/// 而这条路径只在模型显式要看邮件时走一次。
/// </para>
/// <para>
/// **并发 1**（设计 §B3 风险表）：渲染与读原文共用一条闸，别让一次批量追问同时开五条 IMAP。
/// </para>
/// </remarks>
public sealed class MailImapAccess
{
    private readonly IImapSessionFactory _sessions;
    private readonly IMailRepository _repository;
    private readonly ICredentialProtector _protector;
    private readonly ILogger _logger;

    /// <summary>全局串行闸：同一时刻最多一条按需 IMAP 连接。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MailImapAccess(
        IImapSessionFactory sessions,
        IMailRepository repository,
        ICredentialProtector protector,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);

        _sessions = sessions;
        _repository = repository;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>取一封邮件的 HTML 源文本；这封没有 HTML 部分时返回 <see langword="null"/>。</summary>
    public Task<string?> GetHtmlAsync(string accountId, long uid, int maxChars, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return WithSessionAsync(
            accountId,
            (session, token) => session.GetHtmlAsync(uid, maxChars, token),
            ct);
    }

    /// <summary>取一封邮件的原文（头 + 正文源文本）。</summary>
    public Task<MailRawContent> GetRawAsync(string accountId, long uid, int maxChars, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return WithSessionAsync(
            accountId,
            (session, token) => session.GetRawAsync(uid, maxChars, token),
            ct);
    }

    /// <summary>「找账号 → 解密凭据 → 建连 → 干活 → 断开」这一整套，抽成一处。</summary>
    /// <remarks>
    /// ⚠️ 断开连接**必须在放闸之后**：<c>DisconnectAsync(quit:true)</c> 在服务器不响应时
    /// 会一直等到 socket 超时，闸被占着的那段时间里所有按需读取都在排队。
    /// 所以这里显式两步：先干完活、放闸，再带着一个很短的超时去断开。
    /// </remarks>
    private async Task<T> WithSessionAsync<T>(
        string accountId,
        Func<IImapSession, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        IImapSession? session = null;
        try
        {
            var account = await FindAccountAsync(accountId, ct).ConfigureAwait(false);
            var credential = _protector.Unprotect(account.SecretCipher);
            if (string.IsNullOrWhiteSpace(credential))
            {
                // 与收信侧同一句口径：明文绝不进日志，只报事实
                throw new InvalidOperationException(
                    "该账号的授权码无法解密（主密钥可能已变更），请在客户端重新录入授权码。");
            }

            session = _sessions.Create();
            await session.ConnectAsync(account, credential, ct).ConfigureAwait(false);

            return await action(session, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            await CloseQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>断开并释放会话：**带超时**，且任何失败都只记 Debug（不污染调用方结果）。</summary>
    private async Task CloseQuietlyAsync(IImapSession? session)
    {
        if (session is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await session.DisconnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "按需读邮件：断开 IMAP 连接失败（忽略）");
        }
        finally
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "按需读邮件：释放 IMAP 会话失败（忽略）");
            }
        }
    }

    private async Task<MailAccount> FindAccountAsync(string accountId, CancellationToken ct)
    {
        var accounts = await _repository.LoadAccountsAsync(ct).ConfigureAwait(false);
        var account = accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.Ordinal));

        if (account is null)
        {
            _logger.Warning("按需读邮件失败：账号 {AccountId} 已不存在", accountId);
            throw new InvalidOperationException($"收信账号 {accountId} 已不存在（可能被删除），这封邮件读不到了。");
        }

        return account;
    }
}
