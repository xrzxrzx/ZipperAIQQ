using System.Diagnostics;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using System.Text;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Messages;

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
    private readonly ChatAgentOptions _options;
    private readonly ILogger _logger;

    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ChatAgent(
        string sessionId,
        IChatClient client,
        IMessageChannel channel,
        IMessageStore store,
        FaceCatalog faces,
        ChatAgentOptions options,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        SessionId = sessionId;
        _client = client;
        _channel = channel;
        _store = store;
        _faces = faces;
        _options = options;
        _logger = logger;
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
        }
    }

    private async Task<AgentRunResult> RunCoreAsync(AgentRunRequest request, CancellationToken ct)
    {
        using var activity = AgentTelemetry.Source.StartActivity("agent.run");
        activity?.SetTag("session.id", SessionId);
        activity?.SetTag("chat.type", request.Chat.Type.ToString());
        activity?.SetTag("hot.window", _history.Count);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _options.SystemPrompt),
        };
        messages.AddRange(_history);

        // 时间注入（FR-15）：每条消息带时间，且每次都给发送者双名
        var userLine = $"[{BeijingTime.Format(request.Now)}] {request.Sender.DisplayName}：{RenderContent(request)}";
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