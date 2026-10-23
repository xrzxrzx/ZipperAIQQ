using System.Text.Json;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>取某个会话的消息（**含 AI 发出的**，FR-20.3）。</summary>
/// <remarks>
/// 支持 <c>from</c> / <c>to</c>（Unix 毫秒，含端点）：会话页按区间导出时用它取数（#37），
/// 不再受「上次加载了多少条」限制。
/// </remarks>
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

        var limit = Math.Clamp(PayloadReader.GetInt(payload, "limit", 50), 1, 5000);
        var from = ReadTime(payload, "from");
        var to = ReadTime(payload, "to");

        if (from is not null && to is not null && from > to)
        {
            throw new ApiException(ApiResponse.BadRequest, "from 不能晚于 to。");
        }

        var rows = from is null && to is null
            ? await _messages.GetRecentAsync(sessionId, Math.Min(limit, 200), ct).ConfigureAwait(false)
            : await _messages.GetRangeAsync(sessionId, from, to, limit, ct).ConfigureAwait(false);

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

    /// <summary>可选的 Unix 毫秒时间；给了非数字就 400，不静默当成"没给"。</summary>
    private static DateTimeOffset? ReadTime(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var ms))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), out var parsed))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(parsed);
        }

        throw new ApiException(ApiResponse.BadRequest, $"{name} 必须是 Unix 毫秒。");
    }
}
