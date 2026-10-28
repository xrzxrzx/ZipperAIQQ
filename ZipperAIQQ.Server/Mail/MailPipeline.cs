using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Mail;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Server.Mail;

/// <summary>
/// 邮件管道：把「收进来的邮件」推进到「摘要完成 → 按节奏汇总 → 转发给主人」。
/// </summary>
/// <remarks>
/// <para>
/// <b>不做的事</b>：收信与落库在 <see cref="MailSource"/> 里做完了（落库在会话内，
/// 这样重启 / 重连都不会丢信也不会重复发事件）。本类只负责摘要与转发。
/// </para>
/// <para>
/// <b>为什么是「数据库即队列」而不是「事件驱动」</b>：与 <c>SchedulePoller</c> 同一套理由
/// （FR-14 / D21）。事件只用来**提前唤醒**，真正的待办从 <see cref="IMailRepository.GetPendingSummaryAsync"/>
/// 查出来 —— 这样进程中途重启、摘要当天失败、汇总当天没转出去，下一轮都还能补上；
/// 纯事件驱动一旦丢一个事件，那封邮件就永远停在「没摘要」状态，而且没人知道。
/// </para>
/// <para>
/// <b>邮件不进对话链路</b>：不落 <c>Messages</c> 表、不进 Agent、不触发回复
/// （邮件接入设计 §1.2；需求决策九）。
/// </para>
/// </remarks>
public sealed class MailPipeline : BackgroundService
{
    /// <summary>没有新邮件时的巡检间隔：兜底补做「上次没做完的摘要 / 没转出去汇总」。</summary>
    private static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(30);

    /// <summary>单轮最多处理多少封（避免一次醒来把几十封全打给 LLM）。</summary>
    private const int BatchSize = 10;

    private readonly MailSource _source;
    private readonly IMailRepository _repository;
    private readonly IMailSummaryService _summaries;
    private readonly MailDigestService _digest;
    private readonly MailOptions _options;
    private readonly MailOptionsService _switch;
    private readonly ILogger _logger;

    /// <summary>邮件源当前是否已启动（开关热切换时对账用）。</summary>
    private bool _sourceStarted;

    /// <summary>唤醒泵的生命周期：源重启时换一条新的入站流，泵也要跟着重建。</summary>
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    /// <summary>「有新邮件」的唤醒信号（容量 1：多个事件合并成一次唤醒）。</summary>
    /// <remarks>
    /// ⚠️ 必须写全 <c>System.Threading.Channels.Channel</c>：本文件的命名空间是 <c>ZipperAIQQ.Server.Mail</c>，
    /// 而项目里有个 <c>ZipperAIQQ.Channel</c> 命名空间，裸写 <c>Channel.CreateBounded</c> 会被解析到它（CS0234）。
    /// </remarks>
    private readonly Channel<bool> _wakeups = System.Threading.Channels.Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private DateTimeOffset _lastFlushAt = DateTimeOffset.MinValue;
    private DateOnly? _lastDailyFlushDate;

    public MailPipeline(
        MailSource source,
        IMailRepository repository,
        IMailSummaryService summaries,
        MailDigestService digest,
        MailOptions options,
        MailOptionsService switchService,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(digest);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(switchService);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _repository = repository;
        _summaries = summaries;
        _digest = digest;
        _options = options;
        _switch = switchService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastFlushAt = BeijingTime.Now;

        // 开关一变就立刻唤醒，不必等下一轮巡检（默认 30 秒）
        void OnSwitchChanged() => _wakeups.Writer.TryWrite(true);
        _switch.Changed += OnSwitchChanged;

        _logger.Information(
            "邮件管道已启动（开关可热切）：摘要={Summary}（并发 {Concurrency}，重试 {Retries}）汇总={Mode}",
            _options.SummaryEnabled ? "开" : "关",
            _options.MaxConcurrentSummaries,
            _options.SummaryRetryCount,
            _options.DigestMode);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitForWakeupAsync(stoppingToken).ConfigureAwait(false);

                // 每轮对账：开关开 → 起源 + 起泵；开关关 → 停泵 + 停源。
                // 不用"启动时判断一次"——那样客户端点了开关还得重启服务。
                // 源启动失败（读账号时数据库抖动等）只影响本轮：下一轮继续重试，
                // 不能让它把 BackgroundService 带崩（否则摘要/转发会永久停摆到下次重启）。
                try
                {
                    await EnsureSourceStateAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "邮件源启动失败，等待下一轮巡检重试");
                    continue;
                }

                if (!_switch.Enabled)
                {
                    // 停用期间不摘要、不转发：否则"关了还在发汇总"会让人以为开关没生效
                    continue;
                }

                try
                {
                    await RunCycleAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单轮失败不能把管道带崩：下一轮还会从库里重新取待办
                    _logger.Error(ex, "邮件管道单轮处理失败，等待下一次巡检");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停止
        }
        finally
        {
            _switch.Changed -= OnSwitchChanged;
            await StopPumpAsync().ConfigureAwait(false);
            await StopSourceAsync().ConfigureAwait(false);
            _logger.Information("邮件管道已停止");
        }
    }

    /// <summary>让"源 + 唤醒泵"跟上开关状态（幂等，可每轮调用）。</summary>
    private async Task EnsureSourceStateAsync(CancellationToken ct)
    {
        var wanted = _switch.Enabled;

        if (wanted && !_sourceStarted)
        {
            await _source.StartAsync(ct).ConfigureAwait(false);
            _sourceStarted = true;

            // 源重启会换一条新的入站流，旧的泵读的是已 Complete 的流 → 必须重建
            _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _pumpTask = PumpWakeupsAsync(_pumpCts.Token);

            _logger.Information("邮件源已启动（开关：启用）");
            return;
        }

        if (!wanted && _sourceStarted)
        {
            await StopPumpAsync().ConfigureAwait(false);
            await StopSourceAsync().ConfigureAwait(false);
            _logger.Information("邮件源已停止（开关：停用）；已收邮件与账号仍保留");
        }
    }

    private async Task StopPumpAsync()
    {
        var cts = _pumpCts;
        var task = _pumpTask;
        _pumpCts = null;
        _pumpTask = null;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task StopSourceAsync()
    {
        if (!_sourceStarted)
        {
            return;
        }

        _sourceStarted = false;

        // 停机 / 关开关时传入的 token 可能已取消 —— 断开连接必须能跑完
        try
        {
            await _source.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "关闭邮件源时出错（连接可能已断）");
        }
    }

    /// <summary>等一个唤醒信号，或等到空闲巡检时间到。</summary>
    private async Task WaitForWakeupAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IdleTick);

        try
        {
            await _wakeups.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 只是空闲巡检时间到了，不是停机
        }
    }

    /// <summary>把 MailSource 的事件流变成唤醒信号（事件本身不用：落库已在源里完成）。</summary>
    private async Task PumpWakeupsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _source.ReceiveAsync(ct).ConfigureAwait(false))
            {
                if (evt is not IncomingMail)
                {
                    continue;
                }

                // 满了（还没被消费）就丢掉这次唤醒 —— 反正巡检也会把它们捞起来
                _wakeups.Writer.TryWrite(true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            // 源断了不该让管道静默失效：巡检仍在跑，邮件也不会丢（落库在源里）
            _logger.Error(ex, "邮件事件流异常结束：摘要改由定时巡检驱动");
        }
    }

    /// <summary>一轮：补摘要 → 按节奏汇总转发。</summary>
    private async Task RunCycleAsync(CancellationToken ct)
    {
        if (_options.SummaryEnabled)
        {
            await RunSummariesAsync(ct).ConfigureAwait(false);
        }

        if (ShouldFlushDigest(BeijingTime.Now))
        {
            await FlushDigestAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RunSummariesAsync(CancellationToken ct)
    {
        var pending = await _repository.GetPendingSummaryAsync(BatchSize, ct).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return;
        }

        _logger.Information("邮件摘要：本轮待处理 {Count} 封", pending.Count);

        var parallelism = Math.Max(1, _options.MaxConcurrentSummaries);
        if (parallelism == 1)
        {
            foreach (var mail in pending)
            {
                await SummarizeOneAsync(mail, ct).ConfigureAwait(false);
            }

            return;
        }

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = ct,
            },
            async (mail, token) => await SummarizeOneAsync(mail, token).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 摘要一封邮件并回写结果（失败也回写原因，邮件必须保留）。
    /// </summary>
    /// <remarks>
    /// 并发执行时，<c>SqliteMailRepository</c> 的写路径由 <c>ISqliteDatabase</c> 串行化，
    /// 不会出现多写者竞争同一个 SQLite 连接。
    /// </remarks>
    private async Task SummarizeOneAsync(StoredMail mail, CancellationToken ct)
    {
        // 摘要服务自己吞掉所有失败并回填原因（邮件必须保留）——
        // 这里只负责把结果写进库，不替它兜底。
        var summary = await _summaries.SummarizeAsync(ToIncoming(mail), ct).ConfigureAwait(false);

        var status = summary.Error is null ? "ok" : "failed";
        await _repository
            .SaveSummaryAsync(mail.Id, summary.Text, summary.NeedsAction, status, summary.Error, ct)
            .ConfigureAwait(false);

        if (summary.Error is null)
        {
            _logger.Information(
                "邮件摘要完成：#{Id} 主题={Subject} 需处理={NeedsAction}",
                mail.Id, mail.Subject, summary.NeedsAction);
        }
        else
        {
            _logger.Warning("邮件摘要失败（邮件保留）：#{Id} 主题={Subject} 原因={Error}",
                mail.Id, mail.Subject, summary.Error);
        }
    }

    /// <summary>当前是否该汇总转发（按 <see cref="MailOptions.DigestMode"/>）。</summary>
    private bool ShouldFlushDigest(DateTimeOffset now)
    {
        return _options.DigestMode switch
        {
            // 单封即转：每轮都尝试（没有待转的邮件时 FlushAsync 自己会空转返回）
            MailDigestMode.Immediate => true,

            MailDigestMode.Interval => now - _lastFlushAt >= TimeSpan.FromMinutes(_options.DigestIntervalMinutes),

            // 每日：过了设定时刻且今天还没转过（重启后当天会补转一次）
            MailDigestMode.Daily => now.Hour >= _options.DailyAtHour
                && _lastDailyFlushDate != DateOnly.FromDateTime(now.DateTime),

            _ => false,
        };
    }

    private async Task FlushDigestAsync(CancellationToken ct)
    {
        var result = await _digest.FlushAsync(BatchSize, ct).ConfigureAwait(false);

        if (result.Sent)
        {
            _lastFlushAt = BeijingTime.Now;
            if (_options.DigestMode == MailDigestMode.Daily)
            {
                _lastDailyFlushDate = DateOnly.FromDateTime(_lastFlushAt.DateTime);
            }

            _logger.Information("邮件汇总已转发：{Count} 封 → 主人", result.MailCount);
            return;
        }

        // 没转出去（没主人 / 发送失败）**不推进时间戳**：下一轮继续试，不能静默丢。
        if (result.MailCount > 0)
        {
            _logger.Warning("邮件汇总未转发：{Count} 封，原因={Error}", result.MailCount, result.Error);
        }
    }

    /// <summary>
    /// 从库存行还原成摘要服务的入参。
    /// </summary>
    /// <remarks>
    /// 摘要只吃「主题 + 发件人 + 正文」，所以这里的 <c>Chat</c> 只是占位（不参与路由）。
    /// </remarks>
    private static IncomingMail ToIncoming(StoredMail mail) => new()
    {
        Chat = new ChatRef
        {
            Platform = "mail",
            Type = ChatType.Private,
            Id = $"{mail.AccountId}/{mail.MessageId ?? mail.Id.ToString()}",
        },
        OccurredAt = mail.SentAt,
        EventId = mail.MessageId,
        AccountId = mail.AccountId,
        Subject = mail.Subject,
        FromAddress = mail.FromAddress,
        FromName = mail.FromName,
        BodyText = mail.BodyText,
        HasAttachments = mail.HasAttachments,
    };
}
