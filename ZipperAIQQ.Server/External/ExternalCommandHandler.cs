using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Configuration;

namespace ZipperAIQQ.Server.External;

/// <summary>处理外部应用发来的命令（send / subscribe / ping）。</summary>
public sealed class ExternalCommandHandler
{
    private readonly IMessageChannel _channel;
    private readonly ServerOptions _options;
    private readonly ILogger _logger;

    public ExternalCommandHandler(
        IMessageChannel channel,
        ServerOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _channel = channel;
        _options = options;
        _logger = logger;
    }

    public async Task HandleAsync(ExternalSession session, string json, CancellationToken ct)
    {
        if (!ExternalProtocol.TryParse(json, out var document, out var parseError))
        {
            session.Enqueue(ExternalProtocol.Ack(null, false, parseError));
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            // 允许两种形状：扁平 {"type":...} 或 {"type":"cmd","data":{...}}
            var body = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? data
                : root;

            if (!ExternalProtocol.TryGetString(root, "type", out var type) || string.IsNullOrWhiteSpace(type))
            {
                session.Enqueue(ExternalProtocol.Ack(null, false, "缺少 type。"));
                return;
            }

            switch (type)
            {
                case "subscribe":
                    var events = new List<string>();
                    if (body.TryGetProperty("events", out var eventsElement) &&
                        eventsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in eventsElement.EnumerateArray())
                        {
                            var name = item.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                events.Add(name);
                            }
                        }
                    }

                    session.SetSubscriptions(events);
                    session.Enqueue(ExternalProtocol.Ack(null, true));
                    break;

                case "send":
                    await HandleSendAsync(session, body, ct).ConfigureAwait(false);
                    break;

                case "ping":
                    session.Enqueue(ExternalProtocol.Ack(null, true));
                    break;

                default:
                    session.Enqueue(ExternalProtocol.Ack(null, false, $"未知命令：{type}"));
                    break;
            }
        }
    }

    private async Task HandleSendAsync(
        ExternalSession session, JsonElement body, CancellationToken ct)
    {
        var requestId = ExternalProtocol.TryGetString(body, "requestId", out var rid) ? rid : null;

        if (!_options.ExternalAllowSend)
        {
            session.Enqueue(ExternalProtocol.Ack(requestId, false, "外部发消息未开启（external.allowSend=false）。"));
            return;
        }

        if (!ExternalProtocol.TryGetString(body, "session", out var sessionId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            session.Enqueue(ExternalProtocol.Ack(requestId, false, "session 不能为空（qq:p* / qq:g*）。"));
            return;
        }

        if (!SessionRefParser.TryParse(sessionId, _channel.Platform, out var chat, out var sessionError))
        {
            session.Enqueue(ExternalProtocol.Ack(requestId, false, sessionError ?? "session 格式不对。"));
            return;
        }

        if (!IsAllowed(chat.SessionId))
        {
            session.Enqueue(ExternalProtocol.Ack(requestId, false, $"会话 {chat.SessionId} 不在白名单内。"));
            return;
        }

        if (!ExternalProtocol.TryGetString(body, "text", out var text) || string.IsNullOrWhiteSpace(text))
        {
            session.Enqueue(ExternalProtocol.Ack(requestId, false, "text 不能为空。"));
            return;
        }

        try
        {
            await _channel.SendAsync(new OutgoingMessage
            {
                Chat = chat,
                Segments = [new TextSegment(text)],
            }, ct).ConfigureAwait(false);

            _logger.Information("外部应用已发送消息：{Session}", chat.SessionId);
            session.Enqueue(ExternalProtocol.Ack(requestId, true));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "外部应用发送失败：{Session}", chat.SessionId);
            session.Enqueue(ExternalProtocol.Ack(requestId, false, ex.Message));
        }
    }

    private bool IsAllowed(string sessionId)
    {
        var allowed = _options.ExternalAllowedSessions;
        if (allowed.Count == 0)
        {
            return true;
        }

        foreach (var pattern in allowed)
        {
            if (string.Equals(pattern, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pattern.EndsWith('*') &&
                sessionId.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
