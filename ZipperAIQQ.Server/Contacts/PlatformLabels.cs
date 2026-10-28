namespace ZipperAIQQ.Server.Contacts;

/// <summary>
/// 平台标识 → 给人看的标签（<c>qq</c> → 「QQ」、<c>douyin</c> → 「抖音」）。
/// </summary>
/// <remarks>
/// 单点维护：会话标题、平台总览、审计文案都从这里取，别各写一份 switch ——
/// 以前 <c>SessionNameResolver</c> 用 <c>ToUpperInvariant()</c>，抖音会话就显示成「DOUYIN · xxx」，
/// 和「QQ · xxx」不成一套（2026-10-28 用户实测反馈）。
/// </remarks>
public static class PlatformLabels
{
    /// <summary>取中文标签；未登记的平台原样返回（保持可读，不硬编造）。</summary>
    public static string Label(string? platform) => platform?.ToLowerInvariant() switch
    {
        null or "" => "未知平台",
        "qq" => "QQ",
        "douyin" => "抖音",
        _ => platform,
    };
}
