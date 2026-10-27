using System.Text;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Mails;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Mail;

/// <summary>一次汇总转发的结果。</summary>
/// <param name="MailCount">这一批涉及多少封邮件（0 = 本来就没有待转的）。</param>
/// <param name="Sent">是否真的发出去了。**false 时这些邮件仍是待转发状态**（下次还会试）。</param>
/// <param name="Error">没发出去的可读原因（主人未配置 / 发送失败）；成功时为 <c>null</c>。</param>
public sealed record MailDigestResult(int MailCount, bool Sent, string? Error);

/// <summary>
/// 把「摘要成功但还没转发」的邮件合并成一条，转发到主人私聊（需求决策九）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么按「库里查待转」而不是「事件里攒」</b>：进程重启、当天发送失败、
/// 汇总时刻没转出去，这些情况都得能补。待转状态放在库里（<c>MailSummaries.ForwardedAt</c>），
/// 下一轮巡检重新捞出来即可；纯内存攒批一重启就全丢。
/// </para>
/// <para>
/// <b>主人未配置 ≠ 静默丢</b>（验收标准 10）：返回 <see cref="MailDigestResult.Sent"/> = false +
/// 可读原因，且**不标记已转发** —— 配置好主人之后下一轮自动补发。
/// </para>
/// <para>
/// <b>不转发正文全文</b>：只发 时间 / 发件人 / 主题 / 摘要 / 是否需要处理
/// （设计 §2.3）。正文动辄几千字，转发等于刷屏；真要看详情去客户端看。
/// </para>
/// </remarks>
public class MailDigestService
{
    /// <summary>
    /// 汇总转发到哪个平台的主人（决策九：第一期只转 QQ）。
    /// </summary>
    /// <remarks>键由 <see cref="ZipperAIQQ.Server.Access.AccessKeys"/> 统一提供（含旧键回退），不在这里写字面量。</remarks>
    public const string ForwardPlatform = ZipperAIQQ.Server.Access.AccessKeys.DefaultPlatform;

    /// <summary>
    /// qq 主人的**旧键**（S3-C 之前的口径）。
    /// </summary>
    /// <remarks>
    /// 保留是为了兼容既有调用与测试：读主人时 <see cref="ZipperAIQQ.Server.Access.AccessSettings"/>
    /// 会先读新键 <c>access.qq.owner</c>，空了再回退到这个旧键。
    /// </remarks>
    public const string OwnerKey = ZipperAIQQ.Server.Access.AccessKeys.LegacyOwnerKey;

    /// <summary>单批上限的兜底（调用方传再大也不至于一次刷几千行到 QQ）。</summary>
    private const int MaxBatchSize = 200;

    private readonly IRuntimeSettingsStore _settings;
    private readonly IMessageChannel _channel;
    private readonly IMailRepository _repository;
    private readonly MailOptions _options;
    private readonly Agent.IChatAgentPool _agents;
    private readonly ILogger _logger;

    /// <summary>
    /// 汇总消息的"发送者"：这是系统事件，不是某个真人发的（与通知响应同一口径）。
    /// </summary>
    private static readonly SenderInfo SyntheticSender = new()
    {
        UserId = "system",
        Nickname = "邮件助手",
    };

    public MailDigestService(
        IRuntimeSettingsStore settings,
        IMessageChannel channel,
        IMailRepository repository,
        MailOptions options,
        Agent.IChatAgentPool agents,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _channel = channel;
        _repository = repository;
        _options = options;
        _agents = agents;
        _logger = logger;
    }

    /// <summary>
    /// 取一批待转邮件，合并后发到主人私聊；成功则标记已转发。
    /// </summary>
    /// <param name="maxMails">这一批最多几封（小于 1 时按 1 处理）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>
    /// **不抛异常**（取消除外）：管道每轮都会调它，抛出去只会变成"管道崩了"，
    /// 而真正该让用户看到的是"为什么没转出去"——那就在返回值里。
    /// </remarks>
    public async Task<MailDigestResult> FlushAsync(int maxMails, CancellationToken ct = default)
    {
        try
        {
            return await FlushCoreAsync(maxMails, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 停机是正常流程，不吞也不当失败
            throw;
        }
        catch (Exception ex)
        {
            // 仓储 / 设置 / 通道都可能临时抽风：这一批保持待转发，下一轮重试
            _logger.Error(ex, "邮件汇总转发失败");
            return new MailDigestResult(0, false, "汇总转发失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 重试前的等待（指数退避 1s → 2s → 4s，封顶 8s）。抽成虚方法只为单测能把等待压成 0。
    /// </summary>
    protected virtual TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1)));

    private async Task<MailDigestResult> FlushCoreAsync(int maxMails, CancellationToken ct)
    {
        var limit = Math.Clamp(maxMails, 1, MaxBatchSize);
        var pending = await _repository.GetPendingDigestAsync(limit, ct).ConfigureAwait(false);

        // 管道每轮都会调：空转是常态，不该报错也不该刷 Warning
        if (pending.Count == 0)
        {
            return new MailDigestResult(0, false, null);
        }

        var ownerId = await ZipperAIQQ.Server.Access.AccessSettings
            .GetOwnerAsync(_settings, ForwardPlatform, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            // 不静默丢：不标记已转发，配好主人之后下一轮会补发（验收标准 10）
            _logger.Warning(
                "邮件汇总未转发：{Platform} 平台还没有设置主人，{Count} 封保持待转发",
                ForwardPlatform, pending.Count);

            return new MailDigestResult(
                pending.Count,
                false,
                $"还没有设置主人（{ForwardPlatform}），这封汇总没有发出去；邮件都在库里，配置主人后会自动补发。");
        }

        var text = BuildDigest(pending);
        var target = new ChatRef
        {
            Platform = _channel.Platform,
            Type = ChatType.Private,
            Id = ownerId.Trim(),
        };

        // ── 先交给**人格 agent** 去说（用户要求：AI 总结后由人格模拟的 agent 发给主人）──
        // 为什么不让服务直接发：邮件汇总是"通知"，但主人要面对的是**这个人**。
        // 直接发一段格式化文本，语气和人设是断的；交给 agent，它会用当前人设、当前语境去说。
        var handedToAgent = await TryHandToAgentAsync(pending, text, target, ct).ConfigureAwait(false);
        if (!handedToAgent)
        {
            // 人格 agent 没发出去（模型判断不必发 / 调用失败 / 没人设可用）→ 回落直发。
            // 宁可语气平淡一次，也不能让主人收不到（验收标准 10：不静默丢）。
            _logger.Warning("邮件汇总没能由人格 agent 发出，回落为直接发送（{Count} 封）", pending.Count);

            var direct = await SendDirectAsync(text, target, pending.Count, ct).ConfigureAwait(false);
            if (direct is not null)
            {
                return direct;
            }
        }

        var ids = pending.Select(mail => mail.Id).ToList();
        await _repository.MarkForwardedAsync(ids, ct: ct).ConfigureAwait(false);

        _logger.Information(
            "邮件汇总已转发：{Count} 封 → {Session}（{Via}）",
            pending.Count,
            target.SessionId,
            handedToAgent ? "人格 agent" : "直发回落");

        return new MailDigestResult(pending.Count, true, null);
    }

    /// <summary>
    /// 把汇总交给主人私聊的 agent，让**它按人设**说给主人听。
    /// </summary>
    /// <returns>真的发出去了（≥1 条）为 <c>true</c>；否则 <c>false</c>（调用方回落直发）。</returns>
    /// <remarks>
    /// 三条硬约束写进指令里：不漏项、不编造、不把正文全文倒出来。
    /// 模型偶尔会判断"不用回"（返回 0 条）——那不是错误，只是这条路径没成，回落即可。
    /// </remarks>
    private async Task<bool> TryHandToAgentAsync(
        IReadOnlyList<StoredMail> pending,
        string digestText,
        ChatRef target,
        CancellationToken ct)
    {
        var instruction = $"""
            这是一次**邮件汇总**（{pending.Count} 封），请用你自己的语气转告主人。
            要求：
            1. 不要漏封；每封至少说清「谁发的、主题、要不要处理」。
            2. 摘要里没有的信息**不要编**。
            3. 不要把整段原文照抄过来，像人一样整理成一两条消息即可。
            4. 主人没问的事别展开；发完就停。

            下面是汇总数据（系统生成，供你参考）：
            {digestText}
            """;

        try
        {
            var result = await _agents
                .GetOrCreate(target)
                .RunAsync(
                    new Agent.AgentRunRequest
                    {
                        Chat = target,
                        Sender = SyntheticSender,
                        UserText = instruction,
                        Segments = [new TextSegment(instruction)],
                        Now = BeijingTime.Now,
                    },
                    ct)
                .ConfigureAwait(false);

            if (result.MessagesSent > 0)
            {
                return true;
            }

            _logger.Information("邮件汇总：人格 agent 本轮没有发送（MessagesSent=0），走直发回落");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "邮件汇总交给人格 agent 失败，走直发回落");
            return false;
        }
    }

    /// <summary>直发回落：带重试，失败返回结果（<c>null</c> = 发送成功）。</summary>
    private async Task<MailDigestResult?> SendDirectAsync(
        string text,
        ChatRef target,
        int mailCount,
        CancellationToken ct)
    {
        var retries = Math.Max(0, _options.ForwardRetryCount);
        var failureCount = 0;

        while (true)
        {
            try
            {
                await _channel
                    .SendAsync(
                        new OutgoingMessage
                        {
                            Chat = target,
                            Segments = [new TextSegment(text)],
                        },
                        ct)
                    .ConfigureAwait(false);

                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (failureCount >= retries)
                {
                    // 不标记已转发：邮件留在待转队列里，下一轮继续试
                    _logger.Error(
                        ex,
                        "邮件汇总发送失败（已重试 {Retries} 次），{Count} 封保持待转发",
                        failureCount, mailCount);

                    return new MailDigestResult(
                        mailCount,
                        false,
                        $"发送到主人私聊失败：{ex.Message}（已重试 {failureCount} 次，邮件仍待转发）。");
                }

                failureCount++;
                var delay = RetryDelay(failureCount);

                _logger.Warning(
                    "邮件汇总发送失败，第 {Attempt} 次重试：等待 {DelayMs}ms 原因={Reason}",
                    failureCount, (int)delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>组汇总文本：标题 + 每封的 时间/发件人/主题/摘要/是否需处理，**不含正文**。</summary>
    private static string BuildDigest(IReadOnlyList<StoredMail> mails)
    {
        var builder = new StringBuilder();
        builder.Append("📬 邮件汇总（").Append(mails.Count).AppendLine(" 封）");

        var index = 0;
        foreach (var mail in mails)
        {
            index++;

            builder.AppendLine();
            builder.Append(index).Append(". ");

            if (mail.Summary?.NeedsAction == true)
            {
                builder.Append("⚠️ 需要处理｜");
            }

            builder.Append('[').Append(BeijingTime.FormatShort(mail.SentAt)).Append("] ")
                .AppendLine(DescribeSender(mail));
            builder.Append("   主题：").AppendLine(mail.Subject);
            builder.Append("   摘要：").AppendLine(SummaryText(mail.Summary));
        }

        builder.AppendLine();
        builder.Append("（正文不转发，详情请在客户端邮件列表查看）");

        return builder.ToString().TrimEnd();
    }

    /// <summary>摘要位的内容：成功用摘要，失败带原因 —— 失败也要转，别让人以为漏了信。</summary>
    private static string SummaryText(MailSummaryRow? row)
    {
        if (row is null)
        {
            return "（摘要未完成）";
        }

        if (!row.IsOk)
        {
            return string.IsNullOrWhiteSpace(row.Error)
                ? "（摘要失败：原因未知）"
                : $"（摘要失败：{row.Error}）";
        }

        return string.IsNullOrWhiteSpace(row.Text) ? "（摘要为空）" : row.Text;
    }

    private static string DescribeSender(StoredMail mail) =>
        string.IsNullOrWhiteSpace(mail.FromName)
            ? mail.FromAddress
            : $"{mail.FromName} <{mail.FromAddress}>";
}
