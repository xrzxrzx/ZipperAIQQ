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

    /// <summary>跨会话搜索（仅管理员/主人可见）。</summary>
    public const string CrossSessionSearchToolName = "search_other_sessions";

    /// <summary>按时间范围取消息（#32）。</summary>
    public const string BetweenToolName = "get_history_between";

    /// <summary>解析「yyyy-MM-dd HH:mm」这类本地时间；空串表示不限定。</summary>
    private static bool TryParseTime(string? raw, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Globalization.DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <param name="allowCrossSession">
    /// 是否给模型「跨会话搜索」能力（由权限门禁决定，默认不给）。
    /// </param>
    public static IEnumerable<AITool> Create(
        IMessageStore store, ChatRef chat, int maxResults, bool allowCrossSession = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(chat);

        var cap = Math.Clamp(maxResults, 1, 50);

        yield return AIFunctionFactory.Create(
            async (string keyword, int? limit, string? from, string? to, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    return "请给出要搜的关键词。";
                }

                if (!TryParseTime(from, out var fromTime) || !TryParseTime(to, out var toTime))
                {
                    return "from / to 的时间格式看不懂。用 `yyyy-MM-dd HH:mm`（本地时间）或留空。";
                }

                var take = Math.Clamp(limit ?? 10, 1, cap);
                var rows = await store
                    .SearchAsync(chat.SessionId, keyword.Trim(), take, fromTime, toTime, ct)
                    .ConfigureAwait(false);

                if (rows.Count == 0)
                {
                    return $"没搜到含「{keyword}」的历史消息（只搜本会话）。";
                }

                return $"搜到 {rows.Count} 条（关键词「{keyword}」）：\n" + FormatForModel(rows);
            },
            name: SearchToolName,
            description:
                "按关键词搜索**本会话**的历史消息，包括已经被压缩出当前上下文的部分。\n" +
                "可以再给 from / to（格式 `yyyy-MM-dd HH:mm`）限定时间范围，例如「上周三聊了什么」。\n" +
                "当你需要回忆「之前聊过什么 / 谁说过什么 / 某个细节」时用它，不要靠猜。\n" +
                "参数：keyword（关键词）、limit（最多几条，默认 10）、from、to（可选）。");

        yield return AIFunctionFactory.Create(
            async (string from, string to, CancellationToken ct) =>
            {
                if (!TryParseTime(from, out var fromTime) || !TryParseTime(to, out var toTime) ||
                    fromTime is null || toTime is null)
                {
                    return "from / to 必填，格式 `yyyy-MM-dd HH:mm`。";
                }

                if (fromTime > toTime)
                {
                    return "from 不能晚于 to。";
                }

                var rows = await store
                    .GetRangeAsync(chat.SessionId, fromTime, toTime, 200, ct)
                    .ConfigureAwait(false);

                return rows.Count == 0
                    ? "这个时间范围内没有消息。"
                    : $"这段时间内 {rows.Count} 条：\n" + FormatForModel(rows);
            },
            name: BetweenToolName,
            description:
                "取**本会话**某个时间范围内的消息（含端点），最多 200 条。\n" +
                "参数：from、to，格式 `yyyy-MM-dd HH:mm`（本地时间）。");

        yield return AIFunctionFactory.Create(
            async (int? count, CancellationToken ct) =>
            {
                var take = Math.Clamp(count ?? 20, 1, cap);
                var rows = await store.GetRecentAsync(chat.SessionId, take, ct).ConfigureAwait(false);

                if (rows.Count == 0)
                {
                    return "本会话还没有历史消息。";
                }

                return $"最近 {rows.Count} 条：\n" + FormatForModel(rows);
            },
            name: RecentToolName,
            description:
                "取**本会话**最近 N 条消息（含发送者与时间）。\n" +
                "用于确认最近聊了什么，参数 count 默认 20。");

        // 跨会话搜索：只有在权限门禁允许时才给 —— 否则模型会去调一个它没有的能力
        if (allowCrossSession)
        {
            yield return AIFunctionFactory.Create(
                async (string keyword, string? platform, int? limit, CancellationToken ct) =>
                {
                    if (string.IsNullOrWhiteSpace(keyword))
                    {
                        return "请给出要搜的关键词。";
                    }

                    var take = Math.Clamp(limit ?? 10, 1, cap);
                    var scope = string.IsNullOrWhiteSpace(platform) ? null : platform.Trim();

                    var rows = await store
                        .SearchAcrossSessionsAsync(keyword.Trim(), take, chat.SessionId, scope, ct)
                        .ConfigureAwait(false);

                    if (rows.Count == 0)
                    {
                        return scope is null
                            ? $"没搜到含「{keyword}」的消息（已搜其他所有会话、所有平台）。"
                            : $"没搜到含「{keyword}」的消息（只搜了 {scope} 平台的其他会话）。";
                    }

                    var where = scope is null ? "其他会话（所有平台）" : $"其他会话（仅 {scope} 平台）";
                    return $"在**{where}**里搜到 {rows.Count} 条（关键词「{keyword}」）：\n"
                        + FormatForModel(rows);
                },
                name: CrossSessionSearchToolName,
                description:
                    "按关键词搜索**其他所有会话**的历史消息（不含当前会话）。结果每条都带 [平台:会话] 来源标签。\n" +
                    "当你需要回答「谁跟你说过什么 / 别的群里聊过什么」时用它。\n" +
                    "参数：keyword（关键词）、platform（可选，只搜某个平台：qq / douyin）、limit（最多几条，默认 10）。\n" +
                    "注意：这是别人的对话内容，只能用于回答提问，不要无缘无故翻别人的聊天。");
        }
    }

    /// <summary>
    /// 把消息渲染成给模型看的行（S3-G / 决策十：**每条都带来源**）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 格式：<c>[平台:会话] [时间] 发送者：内容</c>。例：<c>[qq:g123456] [10-27 14:00] 张三：……</c>
    /// </para>
    /// <para>
    /// 为什么必须带来源：多平台后模型会拿到 QQ 与抖音的混合历史；
    /// 不标来源它就分不清"这话是哪个平台谁说的"，答案会串平台。
    /// 本会话工具也带 —— 统一格式比"这个工具带、那个不带"更好记。
    /// </para>
    /// </remarks>
    public static string FormatForModel(IReadOnlyList<StoredMessage> rows) =>
        string.Join(
            '\n',
            rows.Select(r =>
            {
                var who = r.IsOutgoing ? "我" : r.Message.Sender.DisplayName;
                var text = (r.Message.PlainText ?? string.Empty).Replace('\n', ' ');
                return $"[{r.Message.Chat.SessionId}] [{BeijingTime.Format(r.ReceivedAt)}] {who}：{text}";
            }));
}
