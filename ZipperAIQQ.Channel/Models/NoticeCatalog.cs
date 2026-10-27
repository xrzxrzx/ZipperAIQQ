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
/// 现状（2026-10-27，S3-E 前置）：<see cref="NoticeType"/> 的 21 个既有类型全部是 QQ / OneBot 语义
/// （戳一戳、入群、群文件…），**追加的 4 个抖音语义类型**（好友申请 / 入群申请 / 成员变化 / 会话同步）
/// 归 <see cref="DouyinPlatform"/>。本表就是"哪些类型属于哪个平台"的唯一真源 ——
/// 「运行与行为」页的分组、开关键的平台段、通知响应的读取口径都从这里推导，
/// **不要在别处再写一份 switch**（两份迟早漂移）。
/// </para>
/// <para>
/// 抖音这 4 个类型的原始类型串（<see cref="IncomingNotice.RawType"/>）由 <c>douyin-sidecar</c> 决定，
/// 本表**只管平台归属**，不负责 RawType 映射；映射在 S3-E 联调时落地。
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
    /// 判定口径：**语义属于哪个平台就归哪个平台**（不是"从哪条连接来的"）。
    /// <see cref="NoticeType.Unknown"/> 归 QQ —— 它是从 QQ（OneBot）事件里兜底出来的，
    /// 且文档明确"原始类型串保留在 <see cref="IncomingNotice.RawType"/>"，排查时看那个。
    /// </remarks>
    public static string PlatformOf(NoticeType type) => type switch
    {
        NoticeType.DouyinFriendRequest
            or NoticeType.DouyinGroupRequest
            or NoticeType.DouyinMemberChanged
            or NoticeType.DouyinSessionSynced => DouyinPlatform,

        _ => QqPlatform,
    };

    /// <summary>某平台上的全部通知类型（供「运行与行为」按平台分组展示）。</summary>
    public static IReadOnlyList<NoticeType> ForPlatform(string platform)
    {
        var target = string.IsNullOrWhiteSpace(platform) ? QqPlatform : platform.Trim().ToLowerInvariant();

        return Enum.GetValues<NoticeType>()
            .Where(type => string.Equals(PlatformOf(type), target, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
