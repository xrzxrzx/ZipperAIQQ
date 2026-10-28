using System.ClientModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Core.Time;

namespace ZipperAIQQ.Server.Mail;

/// <summary>一封邮件的中文摘要结果（邮件接入设计 §3.5）。</summary>
/// <param name="Text">≤3 句中文要点；失败时为空串。</param>
/// <param name="NeedsAction">是否需要我处理（截止日期 / 发票 / 合同 / 需回复 / 安全问题）。</param>
/// <param name="Error">
/// 失败原因；成功时为 <c>null</c>。
/// **有 <see cref="Error"/> 不等于丢信**：邮件早就在库里，这里只是说"摘要没做成"。
/// </param>
public sealed record MailSummary(string Text, bool NeedsAction, string? Error);

/// <summary>邮件摘要服务：单封邮件 → ≤3 句中文要点 + 是否需要处理。</summary>
/// <remarks>
/// 实现（<see cref="MailSummaryService"/>）**在任何失败下都返回结果、绝不抛异常**，
/// 因为调用方（<c>MailPipeline</c>）拿到返回值才落摘要状态；抛出去会让这封邮件
/// 永远停在"没摘要"状态、而且没人知道为什么。
/// </remarks>
public interface IMailSummaryService
{
    /// <summary>
    /// 摘要一封邮件。**不抛异常**（取消除外：取消是正常流程）。
    /// </summary>
    /// <param name="mail">入参只用「主题 + 发件人 + 正文截断」，不带附件、不带 HTML 原文。</param>
    /// <param name="ct">取消令牌。</param>
    Task<MailSummary> SummarizeAsync(IncomingMail mail, CancellationToken ct = default);
}

/// <summary>
/// 用 LLM 给邮件做中文摘要（邮件接入设计 §6）。
/// </summary>
/// <remarks>
/// <para>
/// <b>独立 prompt，不进入任何对话上下文</b>：摘要是一次性请求，跟主人的聊天历史无关，
/// 也绝不写回会话 —— 邮件是通知流，不是对话流（需求决策九）。
/// </para>
/// <para>
/// <b>为什么这里不用现成的 Agent 摘要链路</b>：<c>SummaryService</c> 压的是对话热窗，
/// 目标是"记住聊过什么"；邮件摘要是给人看的通知，关心的是"要不要我处理"。
/// 两条链路的 prompt、重试策略、失败语义都不同，混在一起会互相拖累。
/// </para>
/// <para>
/// <b>失败语义</b>（设计 §6.3）：
/// 429 / 5xx → 指数退避重试 <see cref="MailOptions.SummaryRetryCount"/> 次；
/// 其他 4xx → 不重试（参数或认证错了，重试只是浪费配额）；
/// JSON 解析失败 → 重试 1 次；全部失败 → 返回 <see cref="MailSummary.Error"/>，
/// <b>邮件保留</b>。
/// </para>
/// <para>
/// <b>并发闸门</b>：<see cref="SemaphoreSlim"/> 限制同时进行的摘要数
/// （默认 2），免得一次收到十几封就把 DeepSeek 配额打爆（设计 §6.4）。
/// </para>
/// </remarks>
public class MailSummaryService : IMailSummaryService
{
    /// <summary>
    /// 送进模型的正文上限（字符）。库里的正文已经按 <c>mail.maxBodyChars</c> 截过，
    /// 但摘要不需要那么长 —— 前几千字就足以判断"这是什么事、要不要我处理"，
    /// 再往后只是烧 token。
    /// </summary>
    private const int MaxInputChars = 4_000;

    /// <summary>
    /// 摘要 prompt。**固定约束**见设计 §6.2：输出 JSON、不编造、信息不足就说不足。
    /// </summary>
    private const string SummaryPrompt = """
        你是邮件摘要助手。把用户给你的这一封邮件压缩成中文要点，供主人在 QQ 上快速判断"要不要处理"。

        输出要求（**必须严格遵守**）：
        - 只输出一个 JSON 对象，不要 Markdown 代码块、不要任何解释或前后缀：
          {"summary":"≤3 句中文要点","needsAction":true}
        - summary：最多 3 句中文，说清「谁、什么事、要我做什么（若有）」，不要复述邮件开头的寒暄
        - needsAction：布尔值。以下情况为 true：有截止日期、涉及发票/报销/合同/付款、需要回复或确认、
          涉及账号安全/异常登录/密码变更、需要下载或签署文件。其余为 false
        - **不编造**：邮件里没写的信息一律不要猜（金额、日期、人名都不许补）
        - **信息不足就说不足**：正文为空或只有签名时，summary 直接写「信息不足：正文为空」
        - 不要输出你的推理过程
        """;

    private readonly IChatClientProvider _clients;
    private readonly MailOptions _options;
    private readonly ILogger _logger;

    /// <summary>并发闸门：同时进行的摘要请求数受限（设计 §6.4）。</summary>
    private readonly SemaphoreSlim _gate;

    public MailSummaryService(IChatClientProvider clients, MailOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _options = options;
        _logger = logger;

        // 配 0 / 负数也当 1：宁可串行，也不能让 SemaphoreSlim 抛 "count must be positive"
        var concurrency = Math.Max(1, options.MaxConcurrentSummaries);
        _gate = new SemaphoreSlim(concurrency, concurrency);
    }

    /// <inheritdoc />
    public async Task<MailSummary> SummarizeAsync(IncomingMail mail, CancellationToken ct = default)
    {
        // 「绝不抛」是硬约束：连坏入参也只当成这一封失败，不能把整批邮件卡死
        if (mail is null)
        {
            _logger.Warning("邮件摘要跳过：入参为 null");
            return new MailSummary(string.Empty, false, "邮件内容为空，无法摘要（邮件已保存）。");
        }

        if (!_options.SummaryEnabled)
        {
            // 关掉摘要不算失败：调用方本来就不该调到这里（MailPipeline 自己会判断开关）
            _logger.Debug("邮件摘要跳过：mail.summaryEnabled = false");
            return new MailSummary(string.Empty, false, null);
        }

        if (!_clients.HasClient)
        {
            _logger.Debug("邮件摘要跳过：还没有可用的 LLM 客户端");
            return new MailSummary(string.Empty, false, "未配置可用的 LLM 端点，本次摘要跳过（邮件已保存）。");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SummarizeCoreAsync(mail, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 重试前的等待时间（指数退避：1s → 2s → 4s …… 封顶 8s）。
    /// </summary>
    /// <remarks>
    /// 抽成虚方法的唯一原因是<b>单测</b>：真等 3 秒的用例没人愿意跑，而等 0 秒不影响任何分支。
    /// </remarks>
    protected virtual TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(8, Math.Pow(2, attempt - 1)));

    /// <summary>真正的请求循环：传输重试 + 解析重试，任何失败都落成 <see cref="MailSummary.Error"/>。</summary>
    private async Task<MailSummary> SummarizeCoreAsync(IncomingMail mail, CancellationToken ct)
    {
        var request = BuildRequest(mail);
        var transportRetries = Math.Max(0, _options.SummaryRetryCount);
        var parseRetriesLeft = 1;      // 设计 §6.3：JSON 解析失败重试 1 次
        var transportFailures = 0;

        while (true)
        {
            string raw;
            try
            {
                var response = await _clients.Current
                    .GetResponseAsync(request, cancellationToken: ct)
                    .ConfigureAwait(false);

                raw = response.Text ?? string.Empty;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 停机 / 取消是正常流程，不吞、不当失败
                throw;
            }
            catch (Exception ex)
            {
                var status = HttpStatusOf(ex);

                // 429 之外的 4xx 是参数 / 认证问题：重试只会浪费配额（设计 §6.3）
                if (status is >= 400 and < 500 and not 429)
                {
                    _logger.Warning(
                        "邮件摘要被上游拒绝（不重试）：账号={AccountId} 状态={Status}",
                        mail.AccountId, status);

                    return new MailSummary(
                        string.Empty,
                        false,
                        $"摘要被上游拒绝（HTTP {status}）：{Describe(ex)}（邮件已保存。）");
                }

                if (transportFailures >= transportRetries)
                {
                    _logger.Warning(
                        "邮件摘要失败（已重试 {Retries} 次）：账号={AccountId} 状态={Status} 原因={Reason}",
                        transportFailures, mail.AccountId, status, Describe(ex));

                    return new MailSummary(
                        string.Empty,
                        false,
                        $"摘要失败：{Describe(ex)}（已重试 {transportFailures} 次，邮件已保存）。");
                }

                transportFailures++;
                var delay = RetryDelay(transportFailures);

                _logger.Warning(
                    "邮件摘要第 {Attempt} 次重试：账号={AccountId} 状态={Status} 等待={DelayMs}ms 原因={Reason}",
                    transportFailures, mail.AccountId, status, (int)delay.TotalMilliseconds, Describe(ex));

                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            if (TryParseSummary(raw, out var summary, out var needsAction, out var parseError))
            {
                return new MailSummary(summary, needsAction, null);
            }

            if (parseRetriesLeft > 0)
            {
                parseRetriesLeft--;
                _logger.Warning(
                    "邮件摘要输出无法解析，重试一次：账号={AccountId} 原因={Reason}",
                    mail.AccountId, parseError);

                await Task.Delay(RetryDelay(1), ct).ConfigureAwait(false);
                continue;
            }

            _logger.Warning(
                "邮件摘要失败（输出格式不合规）：账号={AccountId} 原因={Reason}",
                mail.AccountId, parseError);

            return new MailSummary(
                string.Empty,
                false,
                $"摘要输出格式不合规：{parseError}（邮件已保存）。");
        }
    }

    /// <summary>组请求：只放「主题 + 发件人 + 时间 + 正文截断」。</summary>
    /// <remarks>
    /// 刻意**不放附件、不放 HTML 原文**（设计 §6.1）：附件解析本期不做，
    /// HTML 里全是标签和样式，既烧 token 又容易让模型抄一堆 &amp;nbsp;。
    /// </remarks>
    private static List<ChatMessage> BuildRequest(IncomingMail mail)
    {
        var body = new StringBuilder();
        body.Append("主题：").AppendLine(mail.Subject);
        body.Append("发件人：").AppendLine(MailText.DescribeSender(mail.FromName, mail.FromAddress));
        body.Append("时间：").AppendLine(BeijingTime.Format(mail.OccurredAt));

        if (mail.HasAttachments)
        {
            body.AppendLine("（这封邮件带附件，附件内容未提供）");
        }

        body.AppendLine();
        body.AppendLine("正文：");

        var truncated = TruncateForInput(mail.BodyText, MaxInputChars);
        body.Append(truncated.Text);
        if (truncated.IsTruncated)
        {
            body.AppendLine();
            body.Append("（正文过长，已截断到前 ").Append(MaxInputChars).Append(" 字）");
        }

        return
        [
            new ChatMessage(ChatRole.System, SummaryPrompt),
            new ChatMessage(ChatRole.User, body.ToString()),
        ];
    }

    /// <summary>
    /// 解析模型输出：容忍代码围栏与前后废话，只认第一个 <c>{</c> 到最后一个 <c>}</c>。
    /// </summary>
    /// <remarks>
    /// 现实里模型经常回 ```json … ```，或者先来一句"好的，这是摘要："。
    /// 严格按"整段必须是 JSON"解析会让好结果白丢一次（还要多烧一次重试）。
    /// </remarks>
    private static bool TryParseSummary(
        string raw,
        out string summary,
        out bool needsAction,
        out string error)
    {
        summary = string.Empty;
        needsAction = false;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "模型输出为空";
            return false;
        }

        var start = raw.IndexOf('{', StringComparison.Ordinal);
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            error = "模型输出里找不到 JSON 对象";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "模型输出不是 JSON 对象";
                return false;
            }

            if (!root.TryGetProperty("summary", out var summaryElement) ||
                summaryElement.ValueKind != JsonValueKind.String)
            {
                error = "缺少字符串字段 summary";
                return false;
            }

            summary = (summaryElement.GetString() ?? string.Empty).Trim();
            if (summary.Length == 0)
            {
                error = "summary 为空";
                return false;
            }

            // needsAction 缺省或类型不对时按 false —— 宁可少标"要处理"，也不能反过来误报
            if (root.TryGetProperty("needsAction", out var actionElement) &&
                actionElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                needsAction = actionElement.GetBoolean();
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = "不是合法 JSON：" + ex.Message;
            return false;
        }
    }

    /// <summary>按字符截断，且**不把代理对切断**（切一半会变成乱码方块）。</summary>
    private static (string Text, bool IsTruncated) TruncateForInput(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return (text ?? string.Empty, false);
        }

        var cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return (text[..cut], true);
    }

    /// <summary>从异常链里挖 HTTP 状态码；挖不到返回 <c>null</c>（= 当传输错误处理）。</summary>
    /// <remarks>
    /// 两种来源都要认：OpenAI SDK 抛 <see cref="ClientResultException"/>（带 <c>Status</c>），
    /// 更底层则是 <see cref="HttpRequestException"/>（带 <c>StatusCode</c>）。
    /// 不靠 message 文本猜状态码 —— 那是最脆弱的一种写法。
    /// </remarks>
    private static int? HttpStatusOf(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException { StatusCode: { } code }:
                    return (int)code;

                case ClientResultException clientResult:
                    return clientResult.Status;

                case HttpRequestException:
                    break;
            }
        }

        return null;
    }

    /// <summary>把异常压成一句给人看的原因（不进堆栈、不带请求内容）。</summary>
    private static string Describe(Exception ex)
    {
        var message = ex.Message.Trim();
        return message.Length <= 200 ? message : message[..200] + "…";
    }
}
