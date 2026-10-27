using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Mail;

/// <summary>
/// 邮件信息源：IMAP 收信 → 落库 → 产出 <see cref="IncomingMail"/> 事件（**只收不发**）。
/// </summary>
/// <remarks>
/// <para>
/// 实现 <see cref="IMessageSource"/> 而不是 <see cref="IMessageChannel"/>：邮件没有出站，
/// 硬套通道会得到一个永远抛异常的 <c>SendAsync</c>（设计 §2.1）。
/// </para>
/// <para>
/// **账号隔离**：每个账号一个 <see cref="MailAccountSession"/>（独立连接、独立退避、独立水位），
/// 任何一个账号抛异常都只落在它自己的会话里，不影响其他账号（验收标准 4）。
/// </para>
/// <para>
/// 事件先进进程内无界通道缓冲，<see cref="ReceiveAsync"/> 只负责读它：
/// 消费方（Server 的摘要 / 转发管道）慢一点也不会把 IMAP 轮询卡住。
/// </para>
/// </remarks>
public sealed class MailSource : IMessageSource
{
    /// <summary>平台标识，与 <see cref="ChatRef.Platform"/>、<see cref="IncomingMail.Chat"/> 保持一致。</summary>
    public const string PlatformName = "mail";

    /// <summary>停止时等会话收尾的上限；超时就继续关闭（不能把宿主关停拖死）。</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>凭据解密失败时的账号错误（可读原因，**不含任何凭据内容**）。</summary>
    private const string CredentialUnreadableError =
        "凭据无法解密（主密钥可能已变更），请重新录入授权码";

    private readonly IImapSessionFactory _sessions;
    private readonly IMailRepository _repository;
    private readonly ICredentialProtector _protector;
    private readonly MailSourceOptions _options;
    private readonly ILogger _logger;

    // 必须写全 System.Threading.Channels.Channel：命名空间里还有个 ZipperAIQQ.Channel，
    // 在这个命名空间下裸写 Channel 会被解析成那个命名空间。
    // ⚠️ **不是 readonly**：运行时开关邮件接入要能"停了再起"，而 Channel 一旦 Complete 就不能复用，
    //    所以重启时换一条新的入站流（消费者也要跟着重订阅，见 MailPipeline）。
    private Channel<InboundEvent> _channel = CreateChannel();

    private readonly ConcurrentDictionary<string, SessionHandle> _handles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    private CancellationTokenSource? _lifetime;
    private bool _stopped;

    /// <summary>建一条新的入站流（单读者：管道；多写者：每个账号会话）。</summary>
    private static Channel<InboundEvent> CreateChannel() =>
        System.Threading.Channels.Channel.CreateUnbounded<InboundEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

    /// <summary>构造邮件源（不启动，等 <see cref="StartAsync"/>）。</summary>
    /// <param name="sessions">IMAP 会话工厂。</param>
    /// <param name="repository">邮件仓储。</param>
    /// <param name="protector">凭据加解密（解密后只在内存里传给会话）。</param>
    /// <param name="options">运行参数。</param>
    /// <param name="logger">日志。</param>
    public MailSource(
        IImapSessionFactory sessions,
        IMailRepository repository,
        ICredentialProtector protector,
        MailSourceOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _sessions = sessions;
        _repository = repository;
        _protector = protector;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Platform => PlatformName;

    /// <summary>
    /// 启动：加载**启用**的账号并各起一个会话（等价于一次 <see cref="ReloadAsync"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 签名与 <c>IHostedService.StartAsync</c> 一致（本类不实现该接口，由管道启停，见实施微调 M12）。
    /// </para>
    /// <para>
    /// **可重启**（S3-F 补）：客户端把"邮件接入"关掉再打开时，这里会建一条新的入站流。
    /// 旧流在 <see cref="StopAsync"/> 里已经 Complete，不能复用 —— 复用会得到一个"永远读不到东西"的流。
    /// </para>
    /// </remarks>
    public async Task StartAsync(CancellationToken ct)
    {
        await _reloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                _channel = CreateChannel();
                _stopped = false;
                _lifetime?.Dispose();
                _lifetime = null;
                _logger.Information("邮件源重新启动：已建立新的入站流");
            }
        }
        finally
        {
            _reloadGate.Release();
        }

        await ReloadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>停止：取消所有会话、等它们收尾、结束事件流（可重复调用）。</summary>
    /// <remarks>签名与 <c>IHostedService.StopAsync</c> 一致。</remarks>
    public async Task StopAsync(CancellationToken ct)
    {
        // 关闭路径不跟随 ct：宿主给的 token 已经可能在取消中，拿它等闸就等于放弃清理。
        await _reloadGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _lifetime?.Cancel();
            await StopSessionsAsync().ConfigureAwait(false);
            _lifetime?.Dispose();
            _lifetime = null;
            _stopped = true;
            _channel.Writer.TryComplete();
            _logger.Information("邮件源已停止");
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// 热重建：账号增删改后重新加载，**不重启服务**（设计 §3.3）。
    /// </summary>
    /// <remarks>
    /// 实现方式是「全停再全起」：账号数量是个位数，逐账号 diff 的复杂度换不来什么，
    /// 而停掉旧会话能保证改过的凭据 / 主机真的生效（否则会一直用旧连接）。
    /// </remarks>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _reloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                _logger.Debug("邮件源已停止，忽略本次 Reload");
                return;
            }

            await StopSessionsAsync().ConfigureAwait(false);
            _lifetime ??= new CancellationTokenSource();

            var accounts = await _repository.LoadAccountsAsync(ct).ConfigureAwait(false);
            foreach (var account in accounts)
            {
                if (!account.Enabled)
                {
                    _logger.Debug("邮件账号 {AccountId} 已禁用，跳过", account.Id);
                    continue;
                }

                var credential = _protector.Unprotect(account.SecretCipher);
                if (string.IsNullOrEmpty(credential))
                {
                    // 主密钥变了 / 密文坏了：这个账号起不来，但其他账号必须照常工作。
                    _logger.Warning("邮件账号 {AccountId} 的凭据无法解密，跳过该账号", account.Id);
                    await MarkAccountErrorAsync(account, ct).ConfigureAwait(false);
                    continue;
                }

                StartSession(account, credential);
            }

            _logger.Information("邮件源已加载 {Count} 个账号", _handles.Count);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var inboundEvent in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return inboundEvent;
        }
    }

    /// <summary>起一个账号会话（每条会话自带一个 CTS，方便单独取消）。</summary>
    private void StartSession(MailAccount account, string credential)
    {
        var lifetime = _lifetime
            ?? throw new InvalidOperationException("邮件源尚未启动：_lifetime 为空");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var session = new MailAccountSession(
            account,
            credential,
            _sessions,
            _repository,
            _options,
            _channel.Writer,
            _logger);

        // 不 await：这是长跑会话。异常在 RunSessionAsync 里兜住，不靠"任务没人管"。
        var runner = Task.Run(() => RunSessionAsync(session, cts.Token), CancellationToken.None);

        _handles[account.Id] = new SessionHandle(session, cts, runner);
    }

    /// <summary>会话入口的兜底 catch：绝不让异常从长跑任务里逃逸成未观察任务异常。</summary>
    private async Task RunSessionAsync(MailAccountSession session, CancellationToken ct)
    {
        try
        {
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "邮件账号 {AccountId} 会话异常退出", session.AccountId);
        }
    }

    /// <summary>停掉并释放全部会话（<see cref="ReloadAsync"/> 与 <see cref="StopAsync"/> 共用）。</summary>
    private async Task StopSessionsAsync()
    {
        var handles = _handles.Values.ToArray();
        _handles.Clear();
        if (handles.Length == 0)
        {
            return;
        }

        foreach (var handle in handles)
        {
            handle.Cts.Cancel();
        }

        foreach (var handle in handles)
        {
            try
            {
                await handle.Runner.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Warning("邮件账号 {AccountId} 会话停止超时，继续关闭", handle.Session.AccountId);
            }
            catch (OperationCanceledException)
            {
                // 会话自己以取消结束，正常
            }
        }

        foreach (var handle in handles)
        {
            await handle.Session.DisposeAsync().ConfigureAwait(false);
            handle.Cts.Dispose();
        }
    }

    /// <summary>凭据解不开时，把可读原因写进账号状态，让客户端能看见。</summary>
    private async Task MarkAccountErrorAsync(MailAccount account, CancellationToken ct)
    {
        try
        {
            await _repository
                .UpdateSyncStateAsync(
                    account.Id,
                    account.LastUid,
                    account.LastSyncAt,
                    CredentialUnreadableError,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "写入邮件账号 {AccountId} 的错误状态失败", account.Id);
        }
    }

    /// <summary>一个账号的运行期句柄：会话 + 独立取消源 + 长跑任务。</summary>
    private sealed record SessionHandle(
        MailAccountSession Session,
        CancellationTokenSource Cts,
        Task Runner);
}
