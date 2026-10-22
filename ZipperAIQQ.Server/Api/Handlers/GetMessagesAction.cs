using System.Text.Json;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>取某个会话的最近消息（**含 AI 发出的**，FR-20.3）。</summary>
public sealed class GetMessagesAction : IApiAction
{
    private readonly IMessageStore _messages;

    public GetMessagesAction(IMessageStore messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages;
    }

    public string Name => "getMessages";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var sessionId = PayloadReader.GetString(payload, "sessionId");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ApiException(ApiResponse.BadRequest, "缺少 sessionId。");
        }

        var limit = Math.Clamp(PayloadReader.GetInt(payload, "limit", 50), 1, 200);
        var rows = await _messages.GetRecentAsync(sessionId, limit, ct).ConfigureAwait(false);

        return rows
            .Select(r => new
            {
                id = r.Id,
                isOutgoing = r.IsOutgoing,
                sender = r.Message.Sender.DisplayName,
                text = r.Message.PlainText,
                occurredAt = r.Message.OccurredAt.ToUnixTimeMilliseconds(),
            })
            .ToList();
    }
}
