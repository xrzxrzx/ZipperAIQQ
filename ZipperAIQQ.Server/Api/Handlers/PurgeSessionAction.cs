using System.Text.Json;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 删除某个会话的全部消息（<c>purgeSession</c>）—— **不可恢复**。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要：<c>/purge</c> 只是聊天命令（只能在那个会话里发），脏会话（例如改 id 规则前留下的
/// <c>douyin:p0:1:…</c>）在客户端根本点不掉（2026-10-28 用户实测：两个僵尸会话删不掉）。
/// </para>
/// <para>
/// 删完顺带让会话名缓存失效：否则「会话」页可能用旧昵称继续显示一个已经空掉的会话。
/// </para>
/// </remarks>
public sealed class PurgeSessionAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly IMessageStore _messages;
    private readonly ISessionNameResolver _names;
    private readonly IConfigStore _configs;

    public PurgeSessionAction(
        IMessageStore messages,
        ISessionNameResolver names,
        IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(configs);

        _messages = messages;
        _names = names;
        _configs = configs;
    }

    public string Name => "purgeSession";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("sessionId", out var sessionElement) ||
            sessionElement.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 sessionId（字符串）。");
        }

        var sessionId = (sessionElement.GetString() ?? string.Empty).Trim();
        if (sessionId.Length == 0)
        {
            throw new ApiException(ApiResponse.BadRequest, "sessionId 不能为空。");
        }

        var deleted = await _messages.PurgeAsync(sessionId, ct).ConfigureAwait(false);
        _names.Invalidate();

        await _configs
            .AppendAuditAsync("purgeSession", Actor, $"{sessionId} → {deleted} 条", ct)
            .ConfigureAwait(false);

        return new { sessionId, deleted };
    }
}
