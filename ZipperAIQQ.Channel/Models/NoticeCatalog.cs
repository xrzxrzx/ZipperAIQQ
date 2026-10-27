namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 通知类型的**平台归属**（决策六 / S3-D）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不拆枚举（已定 Q6）：枚举值参与存储键（<c>notice.reply:{type}</c>）与落库的
/// <c>Notices.NoticeType</c>（INTEGER），拆成两套枚举会动数据兼容；而"这个类型属于哪个平台"
/// 本质是一张**映射表**，加字段就够了。
/// </para>
/// <para>
/// 现状：<see cref="NoticeType"/> 的 21 个类型**全部是 QQ / OneBot 语义**（戳一戳、入群、群文件…）。
/// 抖音的通知（好友申请、入群申请、成员变化、会话同步）在 S3-E 接入时**新增枚举值**并入本表，
/// 届时本表就是"哪些类型属于哪个平台"的唯一真源。
/// </para>
/// </remarks>
public static class NoticeCatalog
{
    /// <summary>QQ 平台标识（与 <c>ChatRef.Platform</c> 同口径）。</summary>
    public const string QqPlatform = "qq";

    /// <summary>抖音平台标识。</summary>
    public const string DouyinPlatform = "douyin";

    /// <summary>某通知类型属于哪个平台。</summary>
    /// <remarks>
    /// 现有类型全部归 QQ；<see cref="NoticeType.Unknown"/> 也归 QQ（它是从 QQ 事件里兜底出来的，
    /// 且文档明确"原始类型串保留在 RawType"）。
    /// </remarks>
    public static string PlatformOf(NoticeType type) => QqPlatform;

    /// <summary>某平台上的全部通知类型（供「运行与行为」按平台分组展示）。</summary>
    public static IReadOnlyList<NoticeType> ForPlatform(string platform)
    {
        var target = string.IsNullOrWhiteSpace(platform) ? QqPlatform : platform.Trim().ToLowerInvariant();

        return Enum.GetValues<NoticeType>()
            .Where(type => string.Equals(PlatformOf(type), target, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
