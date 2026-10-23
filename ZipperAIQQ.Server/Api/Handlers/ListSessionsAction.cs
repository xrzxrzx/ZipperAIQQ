using System.Text.Json;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>列出「当前在接收」的会话（按最后消息时间倒序）。</summary>
/// <remarks>
/// <para>FR-20.3 的数据源；客户端拿到后可以再对某个会话调 <c>getMessages</c>。</para>
/// <para>
/// 2026-10-23 起返回 <c>displayName</c>（平台 + 昵称 / 群名，问题记录 #2），
/// 并支持 <c>keyword</c> 搜索（#30）。解析失败回落原始 ID，页面不会空白。
/// </para>
/// </remarks>
public sealed class ListSessionsAction : IApiAction
{
    private readonly IMessageStore _messages;
    private readonly ISessionNameResolver _names;

    public ListSessionsAction(IMessageStore messages, ISessionNameResolver names)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(names);

        _messages = messages;
        _names = names;
    }

    public string Name => "listSessions";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var limit = Math.Clamp(PayloadReader.GetInt(payload, "limit", 50), 1, 500);
        var keyword = PayloadReader.GetString(payload, "keyword")?.Trim();

        // 有搜索词时先多取一些再过滤（显示名在服务端才知道，SQL 只能按 SessionId 筛）
        var fetch = string.IsNullOrWhiteSpace(keyword) ? limit : Math.Max(limit, 500);
        var sessions = await _messages.ListSessionsAsync(fetch, ct).ConfigureAwait(false);

        var names = await _names
            .ResolveAsync(sessions.Select(s => s.SessionId).ToList(), ct)
            .ConfigureAwait(false);

        var rows = sessions.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            rows = rows.Where(s =>
                s.SessionId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                names[s.SessionId].Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        return rows.Take(limit)
            .Select(s =>
            {
                var parsed = SessionIds.Parse(s.SessionId);
                return new
                {
                    sessionId = s.SessionId,
                    platform = parsed?.Platform ?? string.Empty,
                    chatType = parsed is null ? 0 : (parsed.Value.IsGroup ? 1 : 0),
                    displayName = names[s.SessionId],
                    messageCount = s.MessageCount,
                    lastMessageAt = s.LastMessageAt.ToUnixTimeMilliseconds(),
                };
            })
            .ToList();
    }
}
