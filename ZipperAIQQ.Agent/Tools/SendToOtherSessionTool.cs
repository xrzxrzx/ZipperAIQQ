using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// <c>send_to_other_session</c> —— 主动给**别的会话**发消息（仅管理员）。
/// </summary>
/// <remarks>
/// <para>
/// 需求：AI 应该像一个跨聊天的实体，而不是只活在当前会话里 ——
/// 能主动去告诉某个人某件事（比如「把主人刚才说的转达给群里」）。
/// </para>
/// <para>
/// <b>三条安全线</b>：
/// </para>
/// <list type="number">
/// <item>权限：<see cref="ToolCapability.SendToOtherSession"/>，硬底线管理员</item>
/// <item>只能发给**有过对话记录**的会话 —— 防止被陌生人一句话指使去骚扰新号</item>
/// <item>每次发送都写审计（发到哪、发了什么）</item>
/// </list>
/// </remarks>
public static class SendToOtherSessionTool
{
    public const string ToolName = "send_to_other_session";

    private const string Actor = "tool:send-other";

    public static IEnumerable<AITool> Create(
        IMessageChannel channel,
        IMessageStore store,
        ToolCallContext context,
        IPermissionGate gate,
        IConfigStore configs,
        int maxResults)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(gate);

        if (!gate.IsAllowed(ToolCapability.SendToOtherSession, context.Role))
        {
            yield break;
        }

        var cap = Math.Clamp(maxResults, 1, 20);

        yield return AIFunctionFactory.Create(
            async (string sessionId, string text, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(text))
                {
                    return "要发给谁、说什么，两个都得给。";
                }

                var decision = gate.Check(ToolCapability.SendToOtherSession, context);
                if (!decision.Allowed)
                {
                    return decision.Reason ?? "没有权限给其他会话发消息。";
                }

                var target = sessionId.Trim();

                // 当前会话请用 send_message（否则会重复发、且绕过会话内合并逻辑）
                if (string.Equals(target, context.Chat.SessionId, StringComparison.Ordinal))
                {
                    return "这就是当前会话，直接用 send_message 就行。";
                }

                // 只允许发给**有过对话记录**的会话：防被一句话指使去骚扰陌生号
                var sessions = await store.ListSessionsAsync(200, ct).ConfigureAwait(false);
                var known = sessions.Any(s => string.Equals(s.SessionId, target, StringComparison.Ordinal));
                if (!known)
                {
                    return $"没有和「{target}」的对话记录，不能主动发。可用 list_sessions 看看有哪些会话。";
                }

                if (!TryParse(target, out var chat))
                {
                    return "会话 id 格式不对，应该像 qq:p123456（私聊）或 qq:g123456（群）。";
                }

                var outgoing = new OutgoingMessage
                {
                    Chat = chat,
                    Segments = [new TextSegment(text.Trim())],
                };

                try
                {
                    await channel.SendAsync(outgoing, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return $"发送失败：{ex.Message}";
                }

                await configs
                    .AppendAuditAsync(
                        "sendToOtherSession",
                        Actor,
                        $"{target}「{Truncate(text.Trim(), 60)}」",
                        ct)
                    .ConfigureAwait(false);

                return $"已发到 {target}。";
            },
            name: ToolName,
            description:
                "主动给**别的会话**（另一个人 / 另一个群）发一条消息。\n" +
                "只能发给已经有对话记录的会话；当前会话请用 send_message。\n" +
                "⚠️ 不要用它刷屏或替别人传话，只在确实有必要时用。\n" +
                "参数：sessionId（会话 id，如 qq:p123456 / qq:g123456）、text（内容）。");
    }

    /// <summary>把 <c>qq:p123</c> / <c>qq:g456</c> 解析回 <see cref="ChatRef"/>。</summary>
    private static bool TryParse(string sessionId, out ChatRef chat)
    {
        chat = null!;

        var colon = sessionId.IndexOf(':');
        if (colon <= 0 || sessionId.Length <= colon + 2)
        {
            return false;
        }

        var platform = sessionId[..colon];
        var kind = sessionId[colon + 1];
        var id = sessionId[(colon + 2)..];

        if (kind != 'p' && kind != 'g')
        {
            return false;
        }

        chat = new ChatRef
        {
            Platform = platform,
            Type = kind == 'g' ? ChatType.Group : ChatType.Private,
            Id = id,
        };

        return true;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
