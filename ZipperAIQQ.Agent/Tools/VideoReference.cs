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
    /// <summary>作品 id 的数字长度范围（实测 19 位；放宽到 8–32 只为兼容老作品）。</summary>
    private const int MinDigits = 8;

    private const int MaxDigits = 32;

    [GeneratedRegex(@"id\s*[=:]\s*(?<id>\d{8,32})", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitId();

    [GeneratedRegex(@"/(?:video|note|slides)/(?<id>\d{8,32})", RegexOptions.IgnoreCase)]
    private static partial Regex VideoPath();

    [GeneratedRegex(@"\d{8,32}")]
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

        // ③ 就是那串数字（允许夹在别的文字里）
        var numberMatch = AnyLongNumber().Match(text);
        if (numberMatch.Success)
        {
            var candidate = numberMatch.Value;
            if (candidate.Length is >= MinDigits and <= MaxDigits)
            {
                itemId = candidate;
                return true;
            }
        }

        return false;
    }
}
