namespace ZipperAIQQ.Core.Time;

/// <summary>
/// 北京时间（UTC+8）统一入口。
/// </summary>
/// <remarks>
/// <para>
/// **存储用 UTC，呈现用北京时间**（FR-15.3）：
/// 数据库里一律存 Unix 毫秒（UTC 绝对量），本类只负责把它们转成给人/给模型看的北京时间。
/// </para>
/// <para>
/// ⚠️ **绝不使用 <see cref="TimeZoneInfo.Local"/> 或 <c>ToLocalTime()</c>**：
/// 开发机、服务器、容器三者的本地时区可能完全不同，
/// 用本地时区会造出「开发机上对、部署后差 8 小时」这类最难查的故障。
/// </para>
/// <para>
/// 时区**不做成配置项**：多一个可配项就多一条「配错了但看起来正常」的路。
/// </para>
/// </remarks>
public static class BeijingTime
{
    /// <summary>北京时区。</summary>
    public static readonly TimeZoneInfo Zone = ResolveZone();

    /// <summary>把任意时刻转换为北京时间。</summary>
    public static DateTimeOffset ToBeijing(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Zone);

    /// <summary>格式化为完整时间（含星期），用于注入模型上下文。</summary>
    /// <example>2026-10-21 09:30:15 周三</example>
    public static string Format(DateTimeOffset value) =>
        ToBeijing(value).ToString("yyyy-MM-dd HH:mm:ss ddd");

    /// <summary>格式化为短时间，用于日志与列表。</summary>
    /// <example>09:30:15</example>
    public static string FormatShort(DateTimeOffset value) =>
        ToBeijing(value).ToString("HH:mm:ss");

    /// <summary>当前北京时间。</summary>
    public static DateTimeOffset Now => ToBeijing(DateTimeOffset.UtcNow);

    private static TimeZoneInfo ResolveZone()
    {
        // Windows 传统 ID 是 "China Standard Time"，IANA 是 "Asia/Shanghai"。
        // .NET 6+ 在 Windows 上通常也能解析 IANA（经 ICU），但两种都试最稳。
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // 换下一个
            }
            catch (InvalidTimeZoneException)
            {
                // 换下一个
            }
        }

        throw new InvalidOperationException(
            "无法解析北京时区（试过 Asia/Shanghai 与 China Standard Time）—— 运行环境缺少时区数据。");
    }
}
