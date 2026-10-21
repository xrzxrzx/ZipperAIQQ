using System.Diagnostics;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using System.Text.RegularExpressions;
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
                        Segments = ParseContent(text, faces),
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
/// <summary>
    /// 匹配 [表情:名字] 或裸写的 [名字]（后者更自然，模型经常这么写）。
    /// 裸写时**只有能查到表情表的名字才转换** —— 否则 [图片][链接] 这类普通方括号会被误伤。
    /// </summary>
    private static readonly Regex FaceToken = new(@"\[(?:表情:)?([^\]]+)\]", RegexOptions.Compiled);

    /// <summary>
    /// 把文本里的 [表情:名称] 或 [名称] 解析成真实消息段。
    /// </summary>
    /// <remarks>
    /// 查不到的**原样保留为文本** —— 静默吞掉会让 AI 以为发出去了，实际对方什么都没看到。
    /// </remarks>
    private static List<MessageSegment> ParseContent(string text, FaceCatalog catalog)
    {
        var result = new List<MessageSegment>();
        var pos = 0;

        foreach (Match m in FaceToken.Matches(text))
        {
            if (m.Index > pos)
            {
                result.Add(new TextSegment(text[pos..m.Index]));
            }

            var name = m.Groups[1].Value.Trim();
            var entry = catalog.GetByName(name);

            if (entry is null)
            {
                result.Add(new TextSegment(m.Value));
            }
            else if (entry.Kind == "system" && !string.IsNullOrEmpty(entry.Id))
            {
                result.Add(new FaceSegment(entry.Id));
            }
            else if (!string.IsNullOrEmpty(entry.File))
            {
                result.Add(new ImageSegment(entry.File, null));
            }
            else
            {
                result.Add(new TextSegment(m.Value));
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
        {
            result.Add(new TextSegment(text[pos..]));
        }

        return result.Count > 0 ? result : [new TextSegment(text)];
    }
}