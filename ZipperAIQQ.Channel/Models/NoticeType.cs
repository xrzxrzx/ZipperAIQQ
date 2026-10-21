namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 通知类型。
/// </summary>
/// <remarks>
/// 首批支持**响应**的是前五个（FR-18.2）；其余类型仍会**记录**但不响应（FR-18.3）。
/// 新增类型时只加枚举值，不改事件结构 —— 这是把通知做成单一类型而非多种记录的理由。
/// </remarks>
public enum NoticeType
{
    /// <summary>未知 / 未映射的类型（保留原始类型字符串在 <see cref="IncomingNotice.RawType"/>）。</summary>
    Unknown = 0,

    /// <summary>戳一戳。**天然涉及两个人**：谁戳了谁。</summary>
    Poke,

    /// <summary>有人入群。</summary>
    GroupIncrease,

    /// <summary>有人退群 / 被踢。</summary>
    GroupDecrease,

    /// <summary>管理员身份变动（设置 / 取消）。</summary>
    GroupAdminChanged,

    /// <summary>群成员被禁言 / 解除禁言。</summary>
    GroupBan,

    /// <summary>消息被撤回。</summary>
    MessageRecalled,

    /// <summary>好友添加（暂只记录）。</summary>
    FriendAdd,

    /// <summary>群文件上传（暂只记录）。</summary>
    GroupFileUpload,
}
