using System.Threading.Channels;
using MailKit.Security;
using Serilog;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Mail;

/// <summary>
/// 单账号的收信会话：**一条独立连接 + 独立退避 + 独立水位**。
/// </summary>
/// <remarks>
/// <para>
/// 为什么按账号切会话而不是共用一个连接：一个账号凭据失效 / 服务端踢连接时，
/// 其余账号必须照常收信（验收标准 4）。异常隔离的边界就到这个类的
/// <see cref="RunAsync"/> 为止 —— 它只把错误写进账号的 <c>LastError</c>，不往上抛。
/// </para>
/// <para>
/// **落库在会话内完成**：<c>AppendAsync</c> 返回主键（= 这次真的新增了）才发事件并推进水位。
/// 重启补拉 / 断线重连都会重复投递同一封邮件，落库幂等 + 事件只在新增时发，
/// 才不会把同一封邮件重复摘要、重复转发到 QQ（邮件接入设计 §5.2 去重）。
/// </para>
/// <para>
/// 认证失败**不重试**：重试只会拿错误凭据反复打服务器（可能触发风控），
/// 正确做法是写一条可读错误、停下等用户改配置（设计 §5.3）。
/// </para>
/// </remarks>
public sealed class MailAccountSession : IAsyncDisposable
{
    /// <summary>每次向服务器要多少封（分页大小）。</summary>
    internal const int FetchPageSize = 100;

    /// <summary>断线重连的初始退避（设计 §5.3：1s → 60s 封顶）。</summary>
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    /// <summary>退避上限。</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly MailAccount _account;
    private readonly string _credential;
    private readonly IImapSessionFactory _sessions;
    private readonly IMailRepository _repository;
    private readonly MailSourceOptions _options;
    private readonly ChannelWriter<InboundEvent> _sink;
    private readonly ILogger _logger;

    /// <summary>单账号串行闸：MailKit 客户端不保证线程安全，轮询与手动同步不能并行。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IImapSession? _session;
    private long _lastUid;
    private DateTimeOffset? _lastSyncAt;
    private bool _stopped;

    /// <summary>当前账号是否挂着一条错误状态（成功一轮后要清掉，见 <see cref="ClearErrorAsync"/>）。</summary>
    private bool _hasError;

    /// <summary>构造一个账号会话（不启动，等 <see cref="RunAsync"/>）。</summary>
    /// <param name="account">账号配置。</param>
    /// <param name="credential">**明文**凭据；只用于连接，绝不写日志、绝不落库。</param>
    /// <param name="sessions">IMAP 会话工厂。</param>
    /// <param name="repository">邮件仓储（落库 + 水位 + 错误状态）。</param>
    /// <param name="options">运行参数。</param>
    /// <param name="sink">事件出口（<see cref="MailSource"/> 的缓冲通道）。</param>
    /// <param name="logger">日志。</param>
    public MailAccountSession(
        MailAccount account,
        string credential,
        IImapSessionFactory sessions,
        IMailRepository repository,
        MailSourceOptions options,
        ChannelWriter<InboundEvent> sink,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(logger);

        _account = account;
        _credential = credential;
        _sessions = sessions;
        _repository = repository;
        _options = options;
        _sink = sink;
        _logger = logger;
        _lastUid = account.LastUid;
        _lastSyncAt = account.LastSyncAt;

        // 上次运行留下的错误状态：这一轮成功后要清掉（而不是让它一直挂在卡片上）
        _hasError = !string.IsNullOrWhiteSpace(account.LastError);
    }

    /// <summary>账号 ID（日志与事件形状用）。</summary>
    public string AccountId => _account.Id;

    /// <summary>是否已因不可恢复错误（认证失败）停止。</summary>
    public bool IsStopped => _stopped;

    /// <summary>
    /// 跑「连接 → 同步 → 等一个轮询间隔 → 再同步」的长跑循环，直到取消或认证失败。
    /// </summary>
    /// <remarks>
    /// 长跑循环里的异常**必须带退避**（编码规范 §3.4）：否则断网时就是每秒刷一条日志。
    /// 正常取消不记错误 —— <see cref="OperationCanceledException"/> 是流程的一部分。
    /// </remarks>
    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = InitialBackoff;

        try
        {
            while (!ct.IsCancellationRequested && !_stopped)
            {
                try
                {
                    await SyncOnceAsync(ct).ConfigureAwait(false);
                    backoff = InitialBackoff;
                    await Task.Delay(_options.PollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // 宿主关闭 / ReloadAsync 重建：正常路径
                }
                catch (AuthenticationException ex)
                {
                    _stopped = true;
                    _logger.Warning(
                        "邮件账号 {AccountId} 认证失败，已停止该账号：{Reason}",
                        _account.Id,
                        ex.Message);
                    await RecordErrorAsync(
                        "认证失败：授权码或登录名可能已失效，请修正后重新加载账号",
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warning(
                        ex,
                        "邮件账号 {AccountId} 同步失败，{Delay} 后重连",
                        _account.Id,
                        backoff);
                    await RecordErrorAsync(DescribeError(ex), CancellationToken.None).ConfigureAwait(false);
                    await CloseConnectionAsync().ConfigureAwait(false);

                    try
                    {
                        await Task.Delay(backoff, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    backoff = TimeSpan.FromSeconds(
                        Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
                }
            }
        }
        finally
        {
            await CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 执行**一次**同步（连接是惰性的，首次调用顺带建连）。
    /// </summary>
    /// <returns>本次**新增**的邮件数（重复投递不计）。</returns>
    /// <remarks>单独暴露是为了让单测能确定性地驱动一轮同步，不必等轮询计时器。</remarks>
    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return 0;
            }

            var session = await EnsureConnectedAsync(ct).ConfigureAwait(false);

            var processed = _lastUid <= 0
                ? await RunInitialSyncAsync(session, ct).ConfigureAwait(false)
                : await RunIncrementalSyncAsync(session, ct).ConfigureAwait(false);

            // 这一轮没抛异常 = 连接与同步都好了：把之前那条错误清掉（否则卡片永远红着旧账）
            await ClearErrorAsync().ConfigureAwait(false);

            return processed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseConnectionAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>首次同步：只取最新一页，按 <see cref="MailSourceOptions.InitialDays"/> 过滤，然后把水位一次对齐。</summary>
    private async Task<int> RunInitialSyncAsync(IImapSession session, CancellationToken ct)
    {
        var newest = await session.FetchSinceAsync(0, FetchPageSize, ct).ConfigureAwait(false);
        var processed = await ProcessBatchAsync(newest, isInitialSync: true, ct).ConfigureAwait(false);

        // 水位直接对齐到「服务器当前最大 UID」：否则邮箱为空、或这一页全被天数过滤掉时，
        // _lastUid 会一直是 0，每个轮询周期都重走首次同步分支（每次重扫一遍）。
        var maxUid = await session.GetMaxUidAsync(ct).ConfigureAwait(false);
        await AdvanceWatermarkAsync(Math.Max(_lastUid, maxUid), ct).ConfigureAwait(false);

        _logger.Information(
            "邮件账号 {AccountId} 首次同步完成，水位 {LastUid}，新增 {Count} 封",
            _account.Id,
            _lastUid,
            processed);

        return processed;
    }

    /// <summary>增量同步：从水位往后翻页，直到取空或取不满一页。</summary>
    private async Task<int> RunIncrementalSyncAsync(IImapSession session, CancellationToken ct)
    {
        var processed = 0;

        while (true)
        {
            var watermarkBefore = _lastUid;
            var batch = await session.FetchSinceAsync(_lastUid, FetchPageSize, ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            // 防御：服务器可能无视范围重投**旧 UID**（实测 163 就会：同一页每分钟原样返回，
            // 水位卡住不动、每轮重拉一次，还顺带把连接拖到被掐）。只处理真正在水位之后的邮件。
            var fresh = batch.Where(m => m.Uid > watermarkBefore).ToList();
            if (fresh.Count == 0)
            {
                _logger.Debug(
                    "邮件账号 {AccountId} 取回的一页全是旧 UID（水位 {LastUid}），跳过本轮",
                    _account.Id,
                    _lastUid);
                break;
            }

            processed += await ProcessBatchAsync(fresh, isInitialSync: false, ct).ConfigureAwait(false);

            // 熔断：这一页没让水位前进就停手。否则会话实现有 bug（返回旧 UID）时会原地打转。
            if (_lastUid <= watermarkBefore)
            {
                _logger.Warning(
                    "邮件账号 {AccountId} 增量同步未推进水位（{LastUid}），停止本轮",
                    _account.Id,
                    _lastUid);
                break;
            }

            if (fresh.Count < FetchPageSize)
            {
                break;
            }
        }

        return processed;
    }

    /// <summary>处理一页邮件：落库 → 新增才发事件 → 推进水位。</summary>
    private async Task<int> ProcessBatchAsync(
        IReadOnlyList<ImapMessage> batch,
        bool isInitialSync,
        CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, _options.InitialDays));
        var processed = 0;
        var watermark = _lastUid;

        // 按 UID 升序处理：水位才是单调的，中途失败也不会跳到更旧的一封后面。
        //
        // 水位表达的命题是：**UID ≤ 水位 的邮件，要么已经在库里，要么被首同步的天数窗口明确跳过**
        // —— 总之不必再拉。所以「已存在」（AppendAsync 返回 null）与「被窗口过滤」都**要**推水位，
        // 否则同一页会每轮重拉、水位直到来新邮件才解套。
        // 「不丢信」由顺序保证（先 Append 成功，再推水位），与「这次是不是新增」无关；
        // 唯一不推水位的情形是 AppendAsync 抛异常 —— 那才是真的没落库，整批中断、下次重拉。
        foreach (var message in batch.OrderBy(m => m.Uid))
        {
            ct.ThrowIfCancellationRequested();

            // 首次同步只收最近 InitialDays 天：按邮件自身的 SentAt 过滤，而不是按服务器 UID
            // （UID 只跟到达顺序有关，跟邮件日期无关）。
            var withinWindow = !isInitialSync || message.SentAt >= cutoff;
            if (withinWindow)
            {
                var mail = BuildMail(message);
                var mailId = await _repository.AppendAsync(mail, ct).ConfigureAwait(false);
                if (mailId is not null)
                {
                    // 只有「真的新增了」才发事件：否则重启补拉会把历史邮件重放一遍，
                    // 下游摘要与 QQ 转发会跟着刷屏。发事件与推水位是两件事，别绑在一起。
                    await _sink.WriteAsync(mail, ct).ConfigureAwait(false);
                    processed++;
                }
            }

            // 新增 / 已存在 / 被窗口跳过 —— 三种情况都算「这条不用再拉」。
            if (message.Uid > watermark)
            {
                watermark = message.Uid;
            }
        }

        await AdvanceWatermarkAsync(watermark, ct).ConfigureAwait(false);
        return processed;
    }

    /// <summary>把 <see cref="ImapMessage"/> 变成通道层的事件形状（Chat / EventId / OccurredAt）。</summary>
    private IncomingMail BuildMail(ImapMessage message)
    {
        var messageId = string.IsNullOrWhiteSpace(message.MessageId) ? null : message.MessageId;

        // 没有 Message-Id 的历史邮件（少见）用 UID 兜底：Chat.Id 必须唯一，否则两封撞一起。
        var key = messageId ?? $"uid:{message.Uid}";

        return new IncomingMail
        {
            Chat = new ChatRef
            {
                Platform = MailSource.PlatformName,
                Type = ChatType.Private,
                Id = $"{_account.Id}/{key}",
            },
            OccurredAt = message.SentAt,
            EventId = messageId,
            AccountId = _account.Id,
            Subject = message.Subject ?? string.Empty,
            FromAddress = message.FromAddress ?? string.Empty,
            FromName = message.FromName,
            BodyText = _options.TruncateBody(message.BodyText),
            HasAttachments = message.HasAttachments,
        };
    }

    /// <summary>建连（惰性 + 失败即丢：坏连接不能留着复用）。</summary>
    private async Task<IImapSession> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_session is { } existing)
        {
            return existing;
        }

        var session = _sessions.Create();
        try
        {
            await session.ConnectAsync(_account, _credential, ct).ConfigureAwait(false);
        }
        catch
        {
            // 连接失败也要释放：否则每次重连尝试都漏一个 ImapClient / socket。
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
            throw;
        }

        _session = session;
        _logger.Information(
            "邮件账号 {AccountId} 已连接 {Host}:{Port}",
            _account.Id,
            _account.ImapHost,
            _account.ImapPort);

        return session;
    }

    /// <summary>推进水位并落库（失败只喊一声：下次追加的幂等会兜住重复事件）。</summary>
    private async Task AdvanceWatermarkAsync(long uid, CancellationToken ct)
    {
        if (uid <= _lastUid)
        {
            return;
        }

        _lastUid = uid;
        _lastSyncAt = DateTimeOffset.UtcNow;

        try
        {
            await _repository
                .UpdateSyncStateAsync(_account.Id, _lastUid, _lastSyncAt, null, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "邮件账号 {AccountId} 保存同步水位失败", _account.Id);
        }
    }

    /// <summary>写一条可读错误到账号状态（**绝不含凭据**）。</summary>
    private async Task RecordErrorAsync(string error, CancellationToken ct)
    {
        _hasError = true;

        try
        {
            await _repository
                .UpdateSyncStateAsync(_account.Id, _lastUid, _lastSyncAt, error, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "邮件账号 {AccountId} 写入错误状态失败", _account.Id);
        }
    }

    /// <summary>
    /// 同步恢复正常后**清掉历史错误**（2026-10-28 修）。
    /// </summary>
    /// <remarks>
    /// 之前只写不清：163 半夜被掐一次连接，客户端卡片就一直红着那条旧错误，
    /// 明明后来已经收信成功了 —— 界面在骗人，比不显示更糟。
    /// 只在"确实有过错误"时才写库，避免每分钟打一次无意义的 UPDATE。
    /// </remarks>
    private async Task ClearErrorAsync()
    {
        if (!_hasError)
        {
            return;
        }

        _hasError = false;

        try
        {
            await _repository
                .UpdateSyncStateAsync(_account.Id, _lastUid, _lastSyncAt, lastError: null, CancellationToken.None)
                .ConfigureAwait(false);

            _logger.Information("邮件账号 {AccountId} 同步已恢复，清除错误状态", _account.Id);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "邮件账号 {AccountId} 清除错误状态失败", _account.Id);
        }
    }

    /// <summary>把异常压成一句可读原因（客户端要显示，不能把整个堆栈塞进 DB）。</summary>
    private static string DescribeError(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length <= 200 ? text : text[..200];
    }

    /// <summary>断开并释放当前连接（幂等）。</summary>
    private async Task CloseConnectionAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null)
        {
            return;
        }

        try
        {
            await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "邮件账号 {AccountId} 断开连接时出错（忽略）", _account.Id);
        }
        finally
        {
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>释放会话，释放本身的异常不能让主循环炸掉。</summary>
    private async Task DisposeQuietlyAsync(IImapSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "邮件账号 {AccountId} 释放会话时出错（忽略）", _account.Id);
        }
    }
}
