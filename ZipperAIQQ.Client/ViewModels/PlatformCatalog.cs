namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 平台码 ↔ 界面文案（**客户端唯一一份**）。
/// </summary>
/// <remarks>
/// <para>
/// 人员与名单 / 通知 / 工具三页都按这份显示。以前三处各存一份字典，
/// 加平台时漏改一页就会出现「切到抖音、界面还显示 QQ 数据」这类真实事故。
/// </para>
/// <para>
/// 未知平台**原样显示**，不抛异常 —— 服务端先加平台、客户端还没更新时也能用。
/// </para>
/// </remarks>
internal static class PlatformCatalog
{
    /// <summary>缺省平台：契约里省略 <c>platform</c> 时服务端按 <c>qq</c> 处理。</summary>
    internal const string DefaultPlatform = "qq";

    /// <summary>已知平台的展示顺序（未知平台排在后面）。</summary>
    internal static readonly string[] KnownOrder = ["qq", "douyin"];

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qq"] = "QQ",
        ["douyin"] = "抖音",
    };

    /// <summary>规范化平台码：空 → 默认平台，统一小写（与服务端平台规范化同口径）。</summary>
    internal static string Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? DefaultPlatform : code.Trim().ToLowerInvariant();

    /// <summary>平台码 → 界面文案；未知平台原样显示。</summary>
    internal static string LabelOf(string? code)
    {
        var normalized = Normalize(code);
        return Labels.TryGetValue(normalized, out var label) ? label : normalized;
    }

    /// <summary>平台码的排序位次（未知平台排在已知平台之后）。</summary>
    internal static int OrderOf(string? code)
    {
        var normalized = Normalize(code);
        var index = Array.FindIndex(
            KnownOrder,
            known => string.Equals(known, normalized, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? KnownOrder.Length : index;
    }
}
