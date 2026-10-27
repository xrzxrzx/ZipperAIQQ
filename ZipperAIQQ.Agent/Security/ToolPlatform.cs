namespace ZipperAIQQ.Agent.Security;

/// <summary>
/// 工具**适用的平台**（决策五 / S3-D）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要这个：工具有三类 —— 通用（文件、搜索、记忆）、需适配（发消息、查历史）、平台专用（戳一戳、表情）。
/// 现在按名字一刀切，抖音接入后会出现"给抖音发戳一戳"这种事：模型以为能做，平台根本不懂。
/// </para>
/// <para>
/// 用**声明 + 过滤**而不是工具内部 <c>if (platform == "qq")</c>：分支散在每个工具里，
/// 新平台一加就得挨个翻一遍；声明集中在一张表里，漏标也只需改一行。
/// </para>
/// </remarks>
[Flags]
public enum ToolPlatform
{
    /// <summary>不属于任何平台（等价于"哪儿都不能用"，正常不该出现）。</summary>
    None = 0,

    /// <summary>QQ（NapCat / OneBot 11 语义）。</summary>
    Qq = 1 << 0,

    /// <summary>抖音（sidecar 接入后才有）。</summary>
    Douyin = 1 << 1,

    /// <summary>通用：所有平台都能用（默认值）。</summary>
    All = Qq | Douyin,
}

/// <summary>平台标识与 <see cref="ToolPlatform"/> 之间的转换。</summary>
/// <remarks>
/// 平台字符串只有一处定义（<c>qq</c> / <c>douyin</c>），与 <c>ChatRef.Platform</c> 同口径。
/// </remarks>
public static class ToolPlatforms
{
    /// <summary>QQ 的平台标识。</summary>
    public const string QqId = "qq";

    /// <summary>抖音的平台标识。</summary>
    public const string DouyinId = "douyin";

    /// <summary>
    /// 把平台字符串转成标志位。
    /// </summary>
    /// <remarks>
    /// <b>未知平台 → <see cref="ToolPlatform.None"/></b>：这时只有"通用工具"（声明为 <see cref="ToolPlatform.All"/>）能用，
    /// 平台专用工具一律不出现。宁可少给，也不要让模型调用一个平台根本不懂的能力
    /// （它会把失败当成功，然后对用户说"戳好了"）。
    /// </remarks>
    public static ToolPlatform Parse(string? platform) => platform?.Trim().ToLowerInvariant() switch
    {
        QqId => ToolPlatform.Qq,
        DouyinId => ToolPlatform.Douyin,
        _ => ToolPlatform.None,
    };

    /// <summary>
    /// 该工具在某个平台上是否可用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <see cref="ToolPlatform.All"/> 表示「**不限定平台**」，不是"这两个平台"：
    /// 它必须对**包括未登记平台在内**的所有平台都成立。
    /// 否则一旦有一个没登记的平台走进来（邮件占位用的 <c>mail</c>、将来别的 sidecar），
    /// <c>All &amp; None == 0</c> 会把**全部工具**判成不可用 —— 连 <c>send_message</c> 都没了，AI 直接哑巴。
    /// </para>
    /// <para>
    /// 这正是"标志位"与"不限定的哨兵值"混用最容易踩的坑：位运算天然会把"全部"算成"没有一个"。
    /// </para>
    /// </remarks>
    public static bool IsAvailable(ToolPlatform declared, string? platform)
    {
        if (declared == ToolPlatform.None)
        {
            // 显式声明"不属于任何平台"：哪儿都不给
            return false;
        }

        if (declared == ToolPlatform.All)
        {
            // 通用工具：任何平台都有（含未登记平台）
            return true;
        }

        // 平台专用：只在自己的平台上出现
        return (declared & Parse(platform)) != 0;
    }
}
