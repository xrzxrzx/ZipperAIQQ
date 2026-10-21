using System.Text.Json;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>列出「当前在接收」的会话（按最后消息时间倒序）。</summary>
/// <remarks>FR-20.3 的数据源；客户端拿到后可以再对某个会话调 <c>getMessages</c>。</remarks>
public sealed class ListSessionsAction : IApiAction
{
    private readonly IMessageStore _messages;

    public ListSessionsAction(IMessageStore messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages;
    }

    public string Name => "listSessions";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var limit = Math.Clamp(PayloadReader.GetInt(payload, "limit", 50), 1, 500);
        var sessions = await _messages.ListSessionsAsync(limit, ct).ConfigureAwait(false);

        return sessions
            .Select(s => new
            {
                sessionId = s.SessionId,
                messageCount = s.MessageCount,
                lastMessageAt = s.LastMessageAt.ToUnixTimeMilliseconds(),
            })
            .ToList();
    }
}
