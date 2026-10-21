using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;
using ZipperAIQQ.Storage.Summaries;

namespace ZipperAIQQ.Agent.Memory;

/// <summary>
/// 把「已经滑出热窗的旧对话」压缩成一段摘要（M1-C 的温层）。
/// </summary>
/// <remarks>
/// <para>
/// **为什么需要它**：热窗只能留最近几十条，再往前就整段消失 ——
/// 表现出来就是「聊了一小时，它忘了你叫什么」。
/// 全留又把上下文撑爆。摘要就是这两者之间那层。
/// </para>
/// <para>
/// **压缩是累积的**：在上一版摘要基础上并入新内容，
/// 所以 <c>CoversUpToId</c> 必须持续推进，否则会重复压缩同一段。
/// </para>
/// <para>
/// **人设不进摘要**：人设是永久的 system 锚点（D4），
/// 压进摘要里既浪费 token 又有被改写失真的风险。
/// </para>
/// </remarks>
public sealed class SummaryService
{
    /// <summary>积压多少条才值得压一次。太小会频繁调模型，太大会让摘要更新滞后。</summary>
    private const int CompressThreshold = 20;

    private const string SummarizePrompt = """
        你是对话摘要器。把下面的聊天记录压缩成一段简要摘要。

        要求：
        - **保留**：人物关系与称呼、重要事实（喜好、约定、承诺、身份信息）
        - **保留**：尚未结束的话题
        - **丢弃**：寒暄、重复内容、无信息量的客套话
        - 用第三人称、简洁陈述
        - 不超过 300 字
        - **只输出摘要正文**，不要任何前缀或解释
        """;

    private readonly IChatClientProvider _clients;
    private readonly IMessageStore _messages;
    private readonly ISummaryStore _summaries;
    private readonly IMetricsStore _metrics;
    private readonly ILogger _logger;

    public SummaryService(
        IChatClientProvider clients,
        IMessageStore messages,
        ISummaryStore summaries,
        IMetricsStore metrics,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _clients = clients;
        _messages = messages;
        _summaries = summaries;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// 尝试压缩一次。没到阈值就直接返回，不发模型请求。
    /// </summary>
    /// <param name="hotWindow">热窗保留的条数，这些**不压**。</param>
    /// <returns>是否真的执行了压缩。</returns>
    public async Task<bool> TryCompressAsync(
        string sessionId,
        int hotWindow,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var existing = await _summaries.GetAsync(sessionId, ct).ConfigureAwait(false);
        var coversUpTo = existing?.CoversUpToId ?? 0;

        // 多取一些，保证「摘要末尾 + 热窗开头」能接上
        var window = await _messages
            .GetRecentAsync(sessionId, hotWindow + CompressThreshold * 2, ct)
            .ConfigureAwait(false);

        var candidates = window.Where(m => m.Id > coversUpTo).ToList();
        if (candidates.Count <= hotWindow)
        {
            return false;
        }

        // 留下热窗，其余参与压缩
        var toCompress = candidates.Take(candidates.Count - hotWindow).ToList();
        if (toCompress.Count < CompressThreshold)
        {
            return false;
        }

        var newSummary = await SummarizeAsync(sessionId, existing?.Summary, toCompress, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(newSummary))
        {
            return false;
        }

        await _summaries
            .SaveAsync(sessionId, newSummary, toCompress[^1].Id, ct)
            .ConfigureAwait(false);

        _logger.Information(
            "会话 {SessionId} 摘要更新：压缩 {Count} 条，覆盖到 #{CoverId}",
            sessionId, toCompress.Count, toCompress[^1].Id);

        return true;
    }

    private async Task<string?> SummarizeAsync(
        string sessionId,
        string? previousSummary,
        IReadOnlyList<StoredMessage> messages,
        CancellationToken ct)
    {
        var body = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            body.AppendLine("【已有摘要】");
            body.AppendLine(previousSummary);
            body.AppendLine();
        }

        body.AppendLine("【新增对话】");
        foreach (var item in messages)
        {
            // 只用纯文本：摘要里放 [图片] 占位没意义，还占字数
            body.Append('[').Append(BeijingTime.Format(item.Message.OccurredAt)).Append("] ")
                .Append(item.Message.Sender.DisplayName).Append('：')
                .AppendLine(item.Message.PlainText);
        }

        var request = new List<ChatMessage>
        {
            new(ChatRole.System, SummarizePrompt),
            new(ChatRole.User, body.ToString()),
        };

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _clients.Current.GetResponseAsync(request, cancellationToken: ct).ConfigureAwait(false);
            sw.Stop();

            // 摘要也烧 token，而且它跑在后台 —— 不记就完全隐形
            await RecordAsync(
                sessionId,
                sw.Elapsed.TotalMilliseconds,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount).ConfigureAwait(false);

            return response.Text?.Trim();
        }
        catch (Exception ex)
        {
            // 压缩失败不影响对话，下一轮再试
            _logger.Warning(ex, "摘要生成失败");
            return null;
        }
    }

    /// <summary>记一行摘要调用的耗时与 token；指标失败不影响压缩结果。</summary>
    private async Task RecordAsync(string sessionId, double elapsedMs, long? inputTokens, long? outputTokens)
    {
        try
        {
            await _metrics
                .RecordAsync(new MetricEntry(sessionId, "summary", elapsedMs, inputTokens, outputTokens))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "指标记录失败：summary");
        }
    }
}
