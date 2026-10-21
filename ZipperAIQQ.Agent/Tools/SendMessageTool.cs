using System.Diagnostics;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// send_message 工具：AI 唯一能把话真正说出去的通道（FR-2）。
/// </summary>
/// <remarks>
/// <para>会话由平台注入，不作为工具参数 —— 否则模型会瞎填，甚至试图发到它看不到的会话。</para>
/// <para>一次调用可发多条（FR-19）；部分失败不重发已成功的，避免重试造成刷屏。</para>
/// <para>通过 onSent 回报本轮实际发出条数，上层据此决定是否兜底发送（FR-2.1）。</para>
/// </remarks>
public static class SendMessageTool
{
    public const string ToolName = "send_message";

    public static AIFunction Create(
        IMessageChannel channel,
        IMessageStore store,
        string botUserId,
        ChatRef chat,
        int maxMessages,
        int minIntervalMs,
        FaceCatalog faces,
        Action<int> onSent)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(onSent);

        var cap = Math.Max(1, maxMessages);
        var interval = Math.Max(0, minIntervalMs);

        return AIFunctionFactory.Create(
            async (string[] messages, CancellationToken ct) =>
            {
                var toSend = (messages ?? [])
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Take(cap)
                    .ToArray();

                if (toSend.Length == 0)
                {
                    return "没有可发送的内容。";
                }

                var sent = 0;
                var failures = new List<string>();

                using var toolActivity = AgentTelemetry.Source.StartActivity("tool.send_message");
                toolActivity?.SetTag("message.count", toSend.Length);
                toolActivity?.SetTag("min.interval.ms", interval);

                foreach (var text in toSend)
                {
                    // 最小发送间隔（防风控）：第一条不等
                    if (sent > 0 && interval > 0)
                    {
                        await Task.Delay(interval, ct).ConfigureAwait(false);
                    }

                    var outgoing = new OutgoingMessage
                    {
                        Chat = chat,
                        Segments = MessageContentParser.Parse(text, faces),
                    };

                    try
                    {
                        await channel.SendAsync(outgoing, ct).ConfigureAwait(false);
                        sent++;

                        // 发出的也要落库（FR-20.3：聊天记录要能看到 AI 说了什么）
                        // 落库失败不影响「已经发出去」这个事实，所以单独 try
                        try
                        {
                            await store.AppendOutgoingAsync(outgoing, botUserId, BeijingTime.Now, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // 只记不抛：消息已经发出去了，落库失败不该让工具报错
                            _ = ex;
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex.Message);
                    }
                }

                if (sent > 0)
                {
                    onSent(sent);
                }

                return failures.Count == 0
                    ? $"已发送 {sent} 条消息。"
                    : $"已发送 {sent} 条；{failures.Count} 条失败：{string.Join("；", failures)}";
            },
            name: ToolName,
            description:
                "向**当前会话**发送消息。只有调用这个工具，你说的话才会真正发出去。" +
                $"可以一次发送多条（最多 {cap} 条），用于模拟真人把一句话分几条发。" +
                "不需要也不能指定发给谁 —— 会话是固定的。");
    }
}