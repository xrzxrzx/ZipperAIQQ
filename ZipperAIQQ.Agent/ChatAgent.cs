using System.Diagnostics;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using System.Text;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Agent.Memory;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;
using ZipperAIQQ.Storage.Personas;
using ZipperAIQQ.Storage.Summaries;

namespace ZipperAIQQ.Agent;

/// <summary>
/// 一个聊天的执行体（需求 D9：一聊天一实例）。
/// </summary>
/// <remarks>
/// <para>同一实例内部**串行**（实例内一把锁）；不同会话并行。</para>
/// <para>
/// M1-B 的上下文是**最小版**：system + 最近 N 条历史 + 当前消息。
/// 三层压缩、检索、人设重注入分别在 M1-C / M1-D 做。
/// </para>
/// </remarks>
public sealed class ChatAgent : IChatAgent
{
    private readonly IChatClient _client;
    private readonly IMessageChannel _channel;
    private readonly IMessageStore _store;
    private readonly FaceCatalog _faces;
    private readonly MemoryRetriever _retriever;
    private readonly SummaryService _summaries;
    private readonly ISummaryStore _summaryStore;
    private readonly IPersonaStore _personas;
    private readonly IMetricsStore _metrics;
    private readonly ChatAgentOptions _options;
    private readonly ILogger _logger;

    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// 人设锚点（D4 的第 1 条 system）：实例存活期间**永不改变**。
    /// </summary>
    /// <remarks>
    /// 它保证「最初设定」始终在上下文最前方；后面那条会随轮次刷新的重注入
    /// 负责「让模型最近又看到一次」。两条各司其职，缺一个都会慢慢漂。
    /// </remarks>
    private string? _anchorPersona;

    public ChatAgent(string sessionId, ChatAgentDependencies deps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(deps);

        SessionId = sessionId;
        _client = deps.Client;
        _channel = deps.Channel;
        _store = deps.Store;
        _faces = deps.Faces;
        _retriever = deps.Retriever;
        _summaries = deps.Summaries;
        _summaryStore = deps.SummaryStore;
        _personas = deps.Personas;
        _metrics = deps.Metrics;
        _options = deps.Options;
        _logger = deps.Logger;
    }

    /// <summary>机器人自身标识（M1-B 用固定值；接真 self_id 后替换）。</summary>
    public const string BotUserId = "bot";

    public string SessionId { get; }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
            TriggerCompressionInBackground();
        }
    }

    private int _compressing;

    /// <summary>
    /// 后台尝试压缩摘要。
    /// </summary>
    /// <remarks>
    /// **不 await**：压缩要调一次模型（1~2 秒），放在回复路径上会让人等两倍时间。
    /// 没到阈值时它自己会立刻返回，几乎零成本。
    /// <para>
    /// **用独立 CancellationToken**：不能用请求的 ct，
    /// 否则请求一结束压缩就被取消，摘要永远压不出来。
    /// </para>
    /// </remarks>
    private void TriggerCompressionInBackground()
    {
        // 同一实例同时只跑一个压缩任务
        if (Interlocked.CompareExchange(ref _compressing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _summaries
                    .TryCompressAsync(SessionId, _options.HotWindowMessages, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "摘要压缩失败（会话 {SessionId}）", SessionId);
            }
            finally
            {
                Interlocked.Exchange(ref _compressing, 0);
            }
        }, CancellationToken.None);
    }

    private async Task<AgentRunResult> RunCoreAsync(AgentRunRequest request, CancellationToken ct)
    {
        using var activity = AgentTelemetry.Source.StartActivity("agent.run");
        activity?.SetTag("session.id", SessionId);
        activity?.SetTag("chat.type", request.Chat.Type.ToString());
        activity?.SetTag("hot.window", _history.Count);

        // 长期记忆检索：热窗只覆盖最近几条，很久前提过的事要靠这个找回来。
        // 检索失败不影响本轮回复 —— 少点记忆总比不回复强。
        IReadOnlyList<Storage.Messages.StoredMessage> related = [];
        try
        {
            related = await _retriever
                .RetrieveAsync(SessionId, request.UserText, topK: 5, excludeRecentCount: _options.HotWindowMessages, ct: ct)
                .ConfigureAwait(false);
            activity?.SetTag("memory.retrieved", related.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "检索失败（会话 {SessionId}），本轮不带长期记忆", SessionId);
        }
        // ── 人设（D4/D12）：会话级覆盖全局，完全替换而非叠加 ──
        var persona = await _personas
            .GetAsync(request.Chat.Platform, request.Chat.Type.ToString(), request.Chat.Id, ct)
            .ConfigureAwait(false);

        // 锚点：首次锁定后**永不改动**，保证「最初设定」一直在最前方
        _anchorPersona ??= persona.Content;

        // 重注入内容 = 人设 + 摘要。
        // 摘要并进这里而不是单独占一条 system —— D4 要求人设 system 恰好两条，
        // 多一条会让人分不清哪个才是"角色设定"。
        var summary = await _summaryStore.GetAsync(SessionId, ct).ConfigureAwait(false);
        var reinject = new StringBuilder(persona.Content);
        if (summary is not null && !string.IsNullOrWhiteSpace(summary.Summary))
        {
            reinject.AppendLine().AppendLine()
                    .AppendLine("【之前对话的摘要（供理解上下文，不必逐条回应）】")
                    .Append(summary.Summary);
            activity?.SetTag("memory.summary", "yes");
        }

        var messages = new List<ChatMessage>
        {
            // ① 锚点 system：永远第一条
            new(ChatRole.System, _anchorPersona),
        };

        messages.AddRange(_history);

        // ② 重注入 system：紧邻当前消息之前。
        //    位置很关键 —— 放在历史前面就退化成"和锚点差不多远"，
        //    起不到「最近又提醒一次」的作用。
        messages.Add(new ChatMessage(ChatRole.System, reinject.ToString()));

        // 时间注入（FR-15）：每条消息带时间，且每次都给发送者双名
        var memoryBlock = BuildMemoryBlock(related);
        var userLine = $"[{BeijingTime.Format(request.Now)}] {request.Sender.DisplayName}：{memoryBlock}{RenderContent(request)}";
        messages.Add(BuildUserMessage(request, userLine));

        // send_message 绑定当前会话（模型无法指定发给谁）
        var sent = 0;
        var tool = SendMessageTool.Create(
            _channel,
            _store,
            BotUserId,
            request.Chat,
            _options.MaxMessagesPerCall,
            _options.MinSendIntervalMs,
            _faces,
            n => sent += n);

        var chatOptions = new ChatOptions
        {
            Tools = [tool, ListFacesTool.Create(_faces)],
            // 温度之类先不调，保持端点默认
        };

        ChatResponse response;
        try
        {
            using var llmActivity = AgentTelemetry.Source.StartActivity("llm.call");
            llmActivity?.SetTag("llm.message.count", messages.Count);
            var llmSw = System.Diagnostics.Stopwatch.StartNew();
            response = await _client.GetResponseAsync(messages, chatOptions, ct).ConfigureAwait(false);
            llmSw.Stop();
            llmActivity?.SetTag("llm.elapsed.ms", llmSw.ElapsedMilliseconds);
            llmActivity?.SetStatus(ActivityStatusCode.Ok);

            // token 用量与耗时落库 —— 客户端要能看「哪类调用最慢 / token 烧多快」
            await _metrics.RecordAsync(new MetricEntry(
                SessionId,
                "llm.call",
                llmSw.Elapsed.TotalMilliseconds,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount), ct).ConfigureAwait(false);

            llmActivity?.SetTag("llm.input.tokens", response.Usage?.InputTokenCount);
            llmActivity?.SetTag("llm.output.tokens", response.Usage?.OutputTokenCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "调用模型失败（会话 {SessionId}）", SessionId);
            return new AgentRunResult(null, 0);
        }

        var finalText = response.Text;

        // ⭐ 兜底发送（FR-2.1）：模型没调工具、但产出了文本 → 直接发出去。
        // 不这么做的话，模型一旦"忘记调工具"，群里就表现为"叫了不理人"。
        if (sent == 0 && !string.IsNullOrWhiteSpace(finalText))
        {
            try
            {
                var fallback = new OutgoingMessage
                {
                    Chat = request.Chat,
                    Segments = [new TextSegment(finalText)],
                };

                await _channel.SendAsync(fallback, ct).ConfigureAwait(false);
                sent = 1;

                try
                {
                    await _store.AppendOutgoingAsync(fallback, BotUserId, request.Now, ct).ConfigureAwait(false);
                }
                catch
                {
                    // 已发出，落库失败不改变事实
                }
                _logger.Information("兜底发送生效（会话 {SessionId}）：模型未调用工具", SessionId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "兜底发送失败（会话 {SessionId}）", SessionId);
            }
        }

        // 更新历史
        _history.Add(new ChatMessage(ChatRole.User, userLine));
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            _history.Add(new ChatMessage(ChatRole.Assistant, finalText));
        }

        TrimHistory();

        activity?.SetTag("messages.sent", sent);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return new AgentRunResult(finalText, sent);
    }

    /// <summary>热窗裁剪（M1-C 会换成真正的三层策略）。</summary>
    private void TrimHistory()
    {
        var max = Math.Max(2, _options.HotWindowMessages);
        if (_history.Count > max)
        {
            _history.RemoveRange(0, _history.Count - max);
        }
        }

    /// <summary>
    /// 把检索到的早期对话渲染成一段前置说明。
    /// </summary>
    /// <remarks>
    /// 明确标成「可能相关」而不是直接混进对话：模型需要知道这些是**参考材料**，
    /// 不一定都要回应，否则会把几天前的话当成本轮刚说的内容接上来。
    /// </remarks>
    private static string BuildMemoryBlock(IReadOnlyList<Storage.Messages.StoredMessage> related)
    {
        if (related.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("【可能相关的早期对话】（供参考，不一定要回应）");
        foreach (var item in related)
        {
            sb.Append('[').Append(BeijingTime.Format(item.Message.OccurredAt)).Append("] ")
              .Append(item.Message.Sender.DisplayName).Append('：')
              .AppendLine(item.Message.PlainText);
        }
        sb.AppendLine();
        sb.Append("【当前消息】");
        return sb.ToString();
    }

    /// <summary>
    /// 构建发给模型的当前消息。**有图片时走多模态**（deepseek-flash 支持视觉输入）。
    /// </summary>
    /// <remarks>
    /// 只对**当前消息**这样做：历史里的图片仍是 <c>[图片]</c> 占位。
    /// 否则每轮都要把所有历史图片重传一遍，token 会迅速烧光，
    /// 而且旧图对当前这轮几乎没有价值。
    /// </remarks>
    private ChatMessage BuildUserMessage(AgentRunRequest request, string textVersion)
    {
        var hasImage = request.Segments.Any(s => s is ImageSegment { Url: not null });
        if (!hasImage)
        {
            return new ChatMessage(ChatRole.User, textVersion);
        }

        var contents = new List<AIContent>
        {
            new TextContent($"[{BeijingTime.Format(request.Now)}] {request.Sender.DisplayName}："),
        };

        foreach (var segment in request.Segments)
        {
            switch (segment)
            {
                case TextSegment text:
                    contents.Add(new TextContent(text.Text));
                    break;

                case FaceSegment face:
                    contents.Add(new TextContent(RenderFace(face)));
                    break;

                case ImageSegment image when Uri.TryCreate(image.Url, UriKind.Absolute, out var uri):
                    contents.Add(new UriContent(uri, "image/jpeg"));
                    break;

                case ImageSegment:
                    contents.Add(new TextContent("[图片]"));
                    break;

                case MentionSegment mention:
                    contents.Add(new TextContent("@" + mention.UserId + " "));
                    break;
            }
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    /// <summary>
    /// 把消息片段渲染成 AI 能读的文本（表情会变成 [微笑] 这类名字）。
    /// </summary>
    /// <remarks>
    /// 不能直接用 PlainText —— 它只含文本段，表情会被静默丢掉，
    /// 表现出来就是「AI 读不了表情」。
    /// </remarks>
    private string RenderContent(AgentRunRequest request)
    {
        // 积压合并：把这一批里每条都渲染成一行，让模型看到完整上下文
        if (request.Batch.Count > 1)
        {
            var lines = request.Batch.Select(m =>
                "[" + BeijingTime.Format(m.OccurredAt) + "] " + m.Sender.DisplayName + "：" +
                RenderSegments(m.Segments, m.PlainText));
            return string.Join("\n", lines);
        }

        return RenderSegments(request.Segments, request.UserText);
    }

    private string RenderSegments(IReadOnlyList<MessageSegment> segments, string fallback)
    {
        if (segments.Count == 0)
        {
            return fallback;
        }

        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            sb.Append(segment switch
            {
                TextSegment text => text.Text,
                FaceSegment face => RenderFace(face),
                ImageSegment => "[图片]",
                MentionSegment mention => "@" + mention.UserId + " ",
                ReplySegment => string.Empty,
                _ => string.Empty,
            });
        }

        var rendered = sb.ToString().Trim();
        return rendered.Length > 0 ? rendered : fallback;
    }

    private string RenderFace(FaceSegment face)
    {
        var entry = _faces.GetById(face.Id);
        return entry is null ? "[表情]" : "[" + entry.Name + "]";
    }
}