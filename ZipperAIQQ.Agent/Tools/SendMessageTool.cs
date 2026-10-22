using System.Diagnostics;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;

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
        Func<CancellationToken, Task> waitInterval,
        FaceCatalog faces,
        IMetricsStore metrics,
        Action<int> onSent)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(waitInterval);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(onSent);

        var cap = Math.Max(1, maxMessages);

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
                toolActivity?.SetTag("throttle.scope", "session");

                var sw = Stopwatch.StartNew();

                foreach (var text in toSend)
                {
                    // 会话级最小间隔：**每次真正发送前**都排一次队。
                    // （以前是 `if (sent > 0)` —— 单条发送、跨轮次完全不等，等于没限速）
                    await waitInterval(ct).ConfigureAwait(false);

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

                sw.Stop();
                await RecordMetricsAsync(metrics, chat.SessionId, sw.Elapsed.TotalMilliseconds).ConfigureAwait(false);

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
    /// 记录一次工具调用的耗时。
    /// </summary>
    /// <remarks>
    /// 一次调用可能发多条消息，所以按**调用**记一行：
    /// 按条记会让表迅速膨胀，而「工具调用慢在哪」本来就该按调用看。
    /// 指标失败不影响发送结果 —— 消息已经发出去了。
    /// </remarks>
    private static async Task RecordMetricsAsync(IMetricsStore metrics, string sessionId, double elapsedMs)
    {
        try
        {
            await metrics
                .RecordAsync(new MetricEntry(sessionId, "tool.send_message", elapsedMs, null, null))
                .ConfigureAwait(false);
        }
        catch
        {
            // 指标是观测，不是功能；失败不上抛
        }
    }
}