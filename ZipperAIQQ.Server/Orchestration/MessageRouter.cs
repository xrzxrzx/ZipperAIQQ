using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Agent;
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
    private readonly ILogger _logger;

    public MessageRouter(
        IMessageChannel channel,
        IMessageStore store,
        IChatAgentPool agents,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(logger);

        _channel = channel;
        _store = store;
        _agents = agents;
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

    private async Task HandleMessageAsync(IncomingMessage msg, CancellationToken ct)
    {
        // ① 先落库（无论是否回复，历史都要留）
        var id = await _store.AppendAsync(msg, ct).ConfigureAwait(false);

        if (msg.Sender.UserId == "unknown")
        {
            _logger.Warning("消息 #{Id} 缺少发送者信息，跳过", id);
            return;
        }

        // ② M1-B 只响应私聊
        if (msg.Chat.Type != ChatType.Private)
        {
            _logger.Debug("群聊消息 #{Id} 已落库（M1-B 暂不回复群聊）", id);
            return;
        }

        // ③ 交给该聊天的 agent
        var agent = _agents.GetOrCreate(msg.Chat);
        var result = await agent.RunAsync(
            new AgentRunRequest
            {
                Chat = msg.Chat,
                Sender = msg.Sender,
                UserText = msg.PlainText,
                Now = msg.OccurredAt,
            },
            ct).ConfigureAwait(false);

        _logger.Information("会话 {Session} 回复完成：发出 {Sent} 条", msg.Chat.SessionId, result.MessagesSent);
    }
}