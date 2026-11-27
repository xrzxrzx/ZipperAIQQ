namespace ZipperAIQQ.Agent.Express;

/// <summary>
/// 档位的对外字符串形态：键值（落库 / 契约）与中文标签（给人和模型看）。
/// </summary>
/// <remarks>
/// <para>
/// 键值只有 <c>daily</c> / <c>close</c> / <c>explicit</c> 三个；解析**不猜** ——
/// 认不出就返回 <c>false</c>，由调用方报错（设计 §7.1：静默回落会让用户以为设置生效了）。
/// </para>
/// <para>
/// 中文标签只用于文案；解析时**也认**它，是因为手写配置文件与客户端输入框都可能给中文，
/// 接受同义写法不会降低严格性（认不出的照样拒绝）。
/// </para>
/// </remarks>
public static class ExpressLevelText
{
    /// <summary>落库 / 契约用的键值。</summary>
    public static string ToKey(ExpressLevel level) => level switch
    {
        ExpressLevel.Close => "close",
        ExpressLevel.Explicit => "explicit",
        _ => "daily",
    };

    /// <summary>给人看的标签（客户端下拉、状态行、日志）。</summary>
    public static string ToLabel(ExpressLevel level) => level switch
    {
        ExpressLevel.Close => "亲近",
        ExpressLevel.Explicit => "露骨",
        _ => "日常",
    };

    /// <summary>
    /// 解析档位：认 <c>daily/close/explicit</c>（大小写不敏感）与中文标签。
    /// </summary>
    /// <returns>认得出返回 <c>true</c>；空值 / 拼错返回 <c>false</c>。</returns>
    public static bool TryParse(string? raw, out ExpressLevel level)
    {
        level = ExpressLevel.Daily;

        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        switch (text.ToLowerInvariant())
        {
            case "daily":
            case "日常":
                level = ExpressLevel.Daily;
                return true;
            case "close":
            case "亲近":
                level = ExpressLevel.Close;
                return true;
            case "explicit":
            case "露骨":
                level = ExpressLevel.Explicit;
                return true;
            default:
                return false;
        }
    }
}
