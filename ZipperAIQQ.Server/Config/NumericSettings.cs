namespace ZipperAIQQ.Server.Config;

/// <summary>
/// 一条可配置的**数值型**运行参数（M2-E 补：这些以前只能改环境变量重启）。
/// </summary>
/// <param name="Key">存储键（RuntimeSettings 表的主键）。</param>
/// <param name="Label">给界面看的中文名。</param>
/// <param name="Min">允许的最小值（含）。</param>
/// <param name="Max">允许的最大值（含）。</param>
/// <param name="Default">未配置时的默认值。</param>
/// <param name="Unit">单位（仅显示用）。</param>
public sealed record NumericSetting(
    string Key,
    string Label,
    double Min,
    double Max,
    double Default,
    string Unit);

/// <summary>
/// 数值参数的**唯一白名单 + 取值范围**。
/// </summary>
/// <remarks>
/// <para>
/// 校验放在服务端、不在 UI：UI 的 NumberBox 只是引导，
/// **真正的边界必须在服务端**（否则一个手写 HTTP 请求就能把冷却时间设成负数）。
/// </para>
/// <para>
/// 键名用 <c>组.参数</c> 的形式，和 <c>replymode:*</c> / <c>notice.reply:*</c> 一起放在 RuntimeSettings 表里。
/// </para>
/// </remarks>
public static class NumericSettingsSchema
{
    public const string SendIntervalKey = "send.intervalMs";
    public const string SendMaxConsecutiveKey = "send.maxConsecutive";
    public const string SendCooldownKey = "send.cooldownMs";
    public const string FileMaxBytesKey = "file.maxBytes";
    public const string HotWindowKey = "context.hotWindow";

    public static readonly IReadOnlyList<NumericSetting> All =
    [
        new(SendIntervalKey, "发送间隔", 0, 10_000, 300, "ms"),
        new(SendMaxConsecutiveKey, "连发上限", 1, 100, 10, "条"),
        new(SendCooldownKey, "连发冷却", 0, 600_000, 10_000, "ms"),
        new(FileMaxBytesKey, "单文件读写上限", 1024, 10 * 1024 * 1024, 256 * 1024, "字节"),
        new(HotWindowKey, "上下文热窗", 4, 100, 20, "条"),
    ];

    public static NumericSetting? Find(string key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    /// <summary>按 schema 校验一个值；越界返回中文原因。</summary>
    public static bool TryValidate(string key, double value, out string? error)
    {
        var setting = Find(key);
        if (setting is null)
        {
            error = $"未知参数：{key}";
            return false;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            error = $"{setting.Label} 必须是有限数值";
            return false;
        }

        if (value < setting.Min || value > setting.Max)
        {
            error = $"{setting.Label} 必须在 {setting.Min} ~ {setting.Max} {setting.Unit} 之间（收到 {value}）";
            return false;
        }

        error = null;
        return true;
    }
}
