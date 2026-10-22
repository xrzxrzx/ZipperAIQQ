using System.ComponentModel;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 查历史消息的工具：<c>search_history</c> / <c>get_recent_history</c>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要：模型上下文只有热窗那十几条，更早的内容它**看不到**。
/// 以前没有查询工具，被问到"我们之前聊过什么"时它只能编 —— 实测抓到过。
/// </para>
/// <para>
/// 只读、只查**本会话**：不做跨会话检索（那属于需要管理员开启的能力）。
/// </para>
/// </remarks>
public static class MessageTools
{
    public const string SearchToolName = "search_history";
    public const string RecentToolName = "get_recent_history";

    public static IEnumerable<AITool> Create(IMessageStore store, ChatRef chat, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(chat);

        var cap = Math.Clamp(maxResults, 1, 50);

        yield return AIFunctionFactory.Create(
            async (string keyword, int? limit, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    return "请给出要搜的关键词。";
                }

                var take = Math.Clamp(limit ?? 10, 1, cap);
                var rows = await store.SearchAsync(chat.SessionId, keyword.Trim(), take, ct).ConfigureAwait(false);

                if (rows.Count == 0)
                {
                    return $"没搜到含「{keyword}」的历史消息（只搜本会话）。";
                }

                return $"搜到 {rows.Count} 条（关键词「{keyword}」）：\n" + Format(rows);
            },
            name: SearchToolName,
            description:
                "按关键词搜索**本会话**的历史消息，包括已经被压缩出当前上下文的部分。\n" +
                "当你需要回忆「之前聊过什么 / 谁说过什么 / 某个细节」时用它，不要靠猜。\n" +
                "参数：keyword（关键词）、limit（最多几条，默认 10）。");

        yield return AIFunctionFactory.Create(
            async (int? count, CancellationToken ct) =>
            {
                var take = Math.Clamp(count ?? 20, 1, cap);
                var rows = await store.GetRecentAsync(chat.SessionId, take, ct).ConfigureAwait(false);

                if (rows.Count == 0)
                {
                    return "本会话还没有历史消息。";
                }

                return $"最近 {rows.Count} 条：\n" + Format(rows);
            },
            name: RecentToolName,
            description:
                "取**本会话**最近 N 条消息（含发送者与时间）。\n" +
                "用于确认最近聊了什么，参数 count 默认 20。");
    }

    private static string Format(IReadOnlyList<StoredMessage> rows) =>
        string.Join(
            '\n',
            rows.Select(r =>
            {
                var who = r.IsOutgoing ? "我" : r.Message.Sender.DisplayName;
                var text = (r.Message.PlainText ?? string.Empty).Replace('\n', ' ');
                return $"[{BeijingTime.Format(r.ReceivedAt)}] {who}：{text}";
            }));
}
