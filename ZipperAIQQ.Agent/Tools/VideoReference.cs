using System.Text.RegularExpressions;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 把模型手里的「视频引用」收窄成作品 id。
/// </summary>
/// <remarks>
/// <para>
/// 模型手里可能是：入站占位符原文 <c>[视频 id=7706296015465071715]</c>、
/// 分享链接 <c>https://www.douyin.com/video/7706296015465071715</c>、
/// 或就是那串数字。三种都得认 —— 让它"自己挑出 id"是最容易出错的一步
/// （19 位数字抄错一位，工具就会去查一条不存在的视频）。
/// </para>
/// <para>
/// ⚠️ 只认 **≥8 位**的连续数字：太短的（<c>2026</c>、<c>15</c>）是年份 / 时长，
/// 认成 id 会得到一条莫名其妙的「视频不存在」。
/// </para>
/// </remarks>
public static partial class VideoReference
{
    /// <summary>显式写法（`id=` / `/video/`）里的数字下限：放宽到 8 只为兼容老作品。</summary>
    private const int MinDigits = 8;

    /// <summary>裸数字兜底的下限：抖音作品 id 是 19 位，8 位会把手机号/日期认成 id。</summary>
    private const int MinBareDigits = 15;

    private const int MaxDigits = 32;

    [GeneratedRegex(@"id\s*[=:]\s*(?<id>\d{8,32})", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitId();

    [GeneratedRegex(@"/(?:video|note|slides)/(?<id>\d{8,32})", RegexOptions.IgnoreCase)]
    private static partial Regex VideoPath();

    // ③ 的兜底比 ①② 更严：嵌在普通句子里的 8 位数字多半是手机号 / 日期（20261126），
    // 而抖音作品 id 是 19 位。只认 ≥15 位，宁可让模型改用 `[视频 id=…]` 或链接。
    [GeneratedRegex(@"\d{15,32}")]
    private static partial Regex AnyLongNumber();

    /// <summary>解析引用；成功时 <paramref name="itemId"/> 是纯数字 id。</summary>
    /// <param name="reference">模型给的引用（可为 null / 空）。</param>
    /// <param name="itemId">解析出的 id（失败为空串）。</param>
    public static bool TryParseItemId(string? reference, out string itemId)
    {
        itemId = string.Empty;

        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var text = reference.Trim();

        // ① 显式写法的 `id=…`：入站占位符就是这种形状
        var explicitMatch = ExplicitId().Match(text);
        if (explicitMatch.Success)
        {
            itemId = explicitMatch.Groups["id"].Value;
            return true;
        }

        // ② 作品链接里的 /video/<id>
        var pathMatch = VideoPath().Match(text);
        if (pathMatch.Success)
        {
            itemId = pathMatch.Groups["id"].Value;
            return true;
        }

        // ③ 就是那串数字（允许夹在别的文字里）；下限 15 位见 <see cref="AnyLongNumber"/>
        var numberMatch = AnyLongNumber().Match(text);
        if (numberMatch.Success)
        {
            var candidate = numberMatch.Value;
            if (candidate.Length is >= MinBareDigits and <= MaxDigits)
            {
                itemId = candidate;
                return true;
            }
        }

        return false;
    }
}
