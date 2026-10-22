using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Memory;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Server.Orchestration;

/// <summary>
/// 消息路由：收事件 → 落库 → 判断是否该回 → 交给对应的 agent。
/// </summary>
/// <remarks>
/// <para>M1-B 只处理**私聊**：群聊消息仍会落库，但不触发回复。</para>
/// <para>回复模式（mention / every）在 M1-C 补；群聊要判断是否 @ 到机器人，需要 self_id。</para>
/// </remarks>
public sealed class MessageRouter : BackgroundService
{
    private readonly IMessageChannel _channel;
    private readonly IMessageStore _store;
    private readonly IChatAgentPool _agents;
    private readonly MemoryRetriever _retriever;
    private readonly Commands.CommandRouter _commands;
    private readonly Agent.Security.IRoleResolver _roles;
    private readonly Agent.IReplyModeDecider _replyModes;
    private readonly Configuration.ServerOptions _options;
    private readonly ILogger _logger;

    public MessageRouter(
        IMessageChannel channel,
        IMessageStore store,
        IChatAgentPool agents,
        MemoryRetriever retriever,
        Commands.CommandRouter commands,
        Agent.Security.IRoleResolver roles,
        Agent.IReplyModeDecider replyModes,
        Configuration.ServerOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(replyModes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _channel = channel;
        _store = store;
        _agents = agents;
        _retriever = retriever;
        _commands = commands;
        _roles = roles;
        _replyModes = replyModes;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _channel.StartAsync(stoppingToken).ConfigureAwait(false);
        _logger.Information("消息路由已启动，等待事件…");

        await foreach (var evt in _channel.ReceiveAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                switch (evt)
                {
                    case IncomingMessage msg:
                        await HandleMessageAsync(msg, stoppingToken).ConfigureAwait(false);
                        break;

                    case IncomingNotice notice:
                        // M1-B 只记录，不响应（响应策略在 M1-C 之后）
                        _logger.Information("通知 {Type}（{RawType}）会话 {Session}",
                            notice.Type, notice.RawType, notice.Chat.SessionId);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单条消息出错不能把整个接收循环带崩
                _logger.Error(ex, "处理事件失败");
            }
        }
    }

/// <summary>同一会话的收集窗口（毫秒）：这段时间内到达的消息合并成一批。</summary>
    private const int CollectWindowMs = 600;

    private sealed class SessionBuffer
    {
        public readonly List<IncomingMessage> Messages = [];
        public bool Processing;
    }

    private readonly ConcurrentDictionary<string, SessionBuffer> _buffers = new(StringComparer.Ordinal);

    private async Task HandleMessageAsync(IncomingMessage msg, CancellationToken ct)
    {
        // ① 先落库（无论是否回复，历史都要留）
        var id = await _store.AppendAsync(msg, ct).ConfigureAwait(false);

        // ② 命令优先（FR-9.1）：命中就执行并回复，**不进 agent**
        //    没命中（比如 "/foo" 这种普通文本）继续走原流程，用户无感知（9.2）
        var role = _roles.Resolve(msg.Sender.UserId);
        if (await _commands.TryExecuteAsync(msg, role, ct).ConfigureAwait(false))
        {
            return;
        }

        // ② 补算向量（M1-C 检索的前提）。
        //    失败不影响本轮回复 —— 顶多是这条以后检索不到，不能因为向量化失败就不回话。
        try
        {
            await _retriever.BackfillAsync(msg.Chat.SessionId, maxBatch: 10, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "向量补算失败（会话 {Session}）", msg.Chat.SessionId);
        }

        if (msg.Sender.UserId == "unknown")
        {
            _logger.Warning("消息 #{Id} 缺少发送者信息，跳过", id);
            return;
        }

        // ③ 回复触发模式（FR-11）：every / mention / jev
        //    ⚠️ 群聊不再硬性跳过 —— 由模式决定（mention 只在被 @ 时回）。
        //    jev 判断失败会**降级为 mention**，不会阻塞、不会丢消息。
        var mode = await _replyModes
            .GetModeAsync(msg.Chat.SessionId, ct)
            .ConfigureAwait(false);

        if (!await _replyModes.ShouldReplyAsync(msg, _options.SelfId ?? string.Empty, ct).ConfigureAwait(false))
        {
            _logger.Debug("回复模式 {Mode}：跳过 #{Id}（会话 {Session}）", mode, id, msg.Chat.SessionId);
            return;
        }

        // ③ 放进该会话的缓冲区；若已有处理循环，它会在下一轮把它一起取走
        var buffer = _buffers.GetOrAdd(msg.Chat.SessionId, _ => new SessionBuffer());
        lock (buffer)
        {
            buffer.Messages.Add(msg);
            if (buffer.Processing)
            {
                return;
            }

            buffer.Processing = true;
        }

        _ = ProcessBatchAsync(msg.Chat, buffer, ct);
    }

    /// <summary>
    /// 处理同一会话的一批消息。
    /// </summary>
    /// <remarks>
    /// **为什么要合并而不是逐条**：连发 5 条会变成 5 次模型调用（每次 1~2 秒），
    /// 而且 AI 会把同一件事分别回 5 遍。合并成一批只调一次，也更像真人的反应。
    /// </remarks>
    private async Task ProcessBatchAsync(ChatRef chat, SessionBuffer buffer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 收集窗口：给后到的消息一点时间并入同一批
                await Task.Delay(CollectWindowMs, ct).ConfigureAwait(false);

                List<IncomingMessage> batch;
                lock (buffer)
                {
                    batch = [.. buffer.Messages];
                    buffer.Messages.Clear();
                }

                if (batch.Count == 0)
                {
                    break;
                }

                var last = batch[^1];
                var agent = _agents.GetOrCreate(chat);

                var result = await agent.RunAsync(new AgentRunRequest
                {
                    Chat = chat,
                    Sender = last.Sender,
                    Now = last.OccurredAt,
                    UserText = last.PlainText,
                    Segments = last.Segments,
                    Batch = batch,
                }, ct).ConfigureAwait(false);

                _logger.Information("会话 {Session} 合并 {Count} 条 → 发出 {Sent} 条",
                    chat.SessionId, batch.Count, result.MessagesSent);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "批处理失败（会话 {Session}）", chat.SessionId);
        }
        finally
        {
            lock (buffer)
            {
                buffer.Processing = false;
                if (buffer.Messages.Count > 0)
                {
                    // 竞态兜底：置空闲的瞬间又来了消息，重新拉起循环，
                    // 否则这些消息会永远躺在缓冲区里没人处理。
                    buffer.Processing = true;
                    _ = ProcessBatchAsync(chat, buffer, ct);
                }
            }
        }
    }
}