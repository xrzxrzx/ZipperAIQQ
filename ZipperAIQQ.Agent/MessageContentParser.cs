using System.Text.RegularExpressions;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Agent;

/// <summary>
/// 把模型输出的**纯文本**变成消息段：解析表情标记，并支持按空行分条。
/// </summary>
/// <remarks>
/// <para>
/// ① <c>send_message</c> 工具（正常路径）与 ② 兜底/降级发送（§8.3 的降级路径）
/// <b>必须共用同一套解析</b>。
/// </para>
/// <para>
/// 兜底曾经自己拼 <c>TextSegment</c>，结果是「<c>[表情:得意]</c> 变成字面量」+
/// 「几句话被 <c>\n\n</c> 塞进同一条消息」——M1-D 的 30 轮实测就是照出来的。
/// 两条路径各写一份解析，这类偏差会一直复发。
/// </para>
/// </remarks>
public static class MessageContentParser
{
    /// <summary>
    /// 匹配 <c>[表情:名字]</c> 或裸写的 <c>[名字]</c>（后者更自然，模型经常这么写）。
    /// </summary>
    /// <remarks>
    /// 裸写时**只有能查到表情表的名字才转换** ——
    /// 否则 <c>[图片]</c> / <c>[链接]</c> 这类普通方括号会被误伤。
    /// </remarks>
    private static readonly Regex FaceToken = new(@"\[(?:表情:)?([^\]]+)\]", RegexOptions.Compiled);

    /// <summary>空行（含空白行）。只切空行，不切单换行。</summary>
    private static readonly Regex BlankLine = new(@"\r?\n\s*\r?\n", RegexOptions.Compiled);

    /// <summary>
    /// 口语化表情名 → 表情表里的正式名。
    /// </summary>
    /// <remarks>
    /// 模型很自然地会写「摊手」「扶额」，但 QQ 官方系统表情里没有这些名字（表里只有 116 个），
    /// 查不到就会把 `[表情:摊手]` 原样发出去 —— 看起来就像"表情没解析"。
    /// 这里给最常见的几个兜个近似项：宁可发一个接近的表情，也别发一串方括号。
    /// </remarks>
    private static readonly Dictionary<string, string> FaceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // 无语类 → 捂脸(264) / 无奈(174)
        ["摊手"] = "捂脸",
        ["扶额"] = "捂脸",
        ["无语"] = "捂脸",
        ["尴尬"] = "尴尬",
        ["汗"] = "流汗",
        ["冒汗"] = "流汗",
        ["冷汗"] = "冷汗",
        ["擦汗"] = "擦汗",

        // 情绪类（**目标名必须是表情表里真实存在的**，否则等于没映射）
        ["好笑"] = "坏笑",
        ["气"] = "生气",
        ["愤怒"] = "生气",
        ["火大"] = "生气",
        ["委屈"] = "委屈",
        ["呜呜"] = "流泪",
        ["想哭"] = "泪奔",
        ["开心"] = "憨笑",
        ["高兴"] = "憨笑",
        ["开心死了"] = "笑哭",
        ["害羞"] = "可爱",
        ["点头"] = "OK",
        ["摇头"] = "NO",
        ["赞"] = "赞",
        ["大拇指"] = "赞",
        ["打哈欠"] = "哈欠",
        ["困"] = "困",
        ["睡觉"] = "睡",
        ["疑问"] = "疑问",
        ["问号"] = "问号脸",
        ["思考"] = "仔细分析",
    };

    /// <summary>
    /// 把文本解析成真实消息段。
    /// </summary>
    /// <remarks>
    /// 查不到的表情名**原样保留为文本** —— 静默吞掉会让 AI 以为发出去了，
    /// 实际对方什么都没看到（那样连"表情没解析"都看不见，更难排查）。
    /// </remarks>
    public static List<MessageSegment> Parse(string text, FaceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var result = new List<MessageSegment>();
        var pos = 0;

        foreach (Match m in FaceToken.Matches(text))
        {
            if (m.Index > pos)
            {
                result.Add(new TextSegment(text[pos..m.Index]));
            }

            var name = m.Groups[1].Value.Trim();

            // [表情包:摘要|emoji=123] —— 模型原样复制就能发回同一张表情包
            if (name.StartsWith("表情包:", StringComparison.Ordinal))
            {
                var body = name["表情包:".Length..];
                var summary = body;
                string? emojiId = null;

                var sep = body.LastIndexOf("|emoji=", StringComparison.Ordinal);
                if (sep >= 0)
                {
                    summary = body[..sep];
                    emojiId = body[(sep + "|emoji=".Length)..].Trim();
                }

                if (!string.IsNullOrEmpty(emojiId))
                {
                    result.Add(new MfaceSegment(emojiId, null, summary));
                }
                else
                {
                    // 没有 id 发不出去 —— 说清楚，别静默丢掉
                    result.Add(new TextSegment($"[表情包:{summary}（没有 id 发不出去）]"));
                }

                pos = m.Index + m.Length;
                continue;
            }
            var entry = catalog.GetByName(name);

            if (entry is null && FaceAliases.TryGetValue(name, out var alias))
            {
                // 模型爱写口语化的表情名（"摊手""扶额"），而 QQ 系统表情没有这些名字。
                // 直接查表查不到 → 以前会原样发出 `[表情:摊手]` 字面量（实测抓到的丑样）。
                entry = catalog.GetByName(alias);
            }

            if (entry is null)
            {
                // `[表情:xxx]` 是明确的"想发表情"意图，查不到就**丢掉标记**（不把字面量发出去）；
                // 裸写的 `[xxx]` 可能是普通方括号，原样保留更安全。
                var isExplicit = m.Value.StartsWith("[表情:", StringComparison.Ordinal);
                if (!isExplicit)
                {
                    result.Add(new TextSegment(m.Value));
                }
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

    /// <summary>
    /// 按**空行**把文本切成多条消息（降级路径用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只切空行、不切单换行：模型常把一句话拆成两行写，那是排版不是"另一条消息"。
    /// <b>空行才是它「我要分两条发」的信号</b>。
    /// </para>
    /// <para>
    /// 超过 <paramref name="maxCount"/> 的部分<b>合并进最后一条</b>，不直接丢弃 ——
    /// 宁可少分几条，也不能让模型说过的话凭空消失。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> SplitByBlankLine(string text, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var cap = Math.Max(1, maxCount);
        var parts = BlankLine.Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (parts.Count == 0)
        {
            return [text.Trim()];
        }

        if (parts.Count <= cap)
        {
            return parts;
        }

        var head = parts.Take(cap - 1).ToList();
        head.Add(string.Join("\n\n", parts.Skip(cap - 1)));
        return head;
    }
}
