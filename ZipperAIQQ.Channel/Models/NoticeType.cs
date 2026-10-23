namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 通知类型（2026-10-23 扩到 21 类，问题记录 #34）。
/// </summary>
/// <remarks>
/// <para>
/// 枚举只负责"这是哪一类"，**原始类型串**仍在 <see cref="IncomingNotice.RawType"/>：
/// NapCat 加新子类型时不必立刻改枚举，先记成 <see cref="Unknown"/> 也能排查。
/// </para>
/// <para>
/// <c>FriendRequest</c> / <c>GroupRequest</c> 是独立大类 <c>request</c>：
/// 只落库、**不自动响应**（自动通过加好友/加群等于替主人做决定）。
/// </para>
/// </remarks>
public enum NoticeType
{
    /// <summary>未知 / 未映射的类型（原始串保留在 <see cref="IncomingNotice.RawType"/>）。</summary>
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

    /// <summary>消息被撤回（群 / 好友）。</summary>
    MessageRecalled,

    /// <summary>好友添加。</summary>
    FriendAdd,

    /// <summary>群文件上传。</summary>
    GroupFileUpload,

    /// <summary>群名片变更（<c>group_card</c>）。</summary>
    GroupCardChanged,

    /// <summary>群名变更（<c>notify / group_name</c>）。</summary>
    GroupNameChanged,

    /// <summary>群头衔变更（<c>notify / title</c>）。</summary>
    GroupTitleChanged,

    /// <summary>群灰条消息（<c>notify / gray_tip</c>）。</summary>
    GroupGrayTip,

    /// <summary>资料点赞（<c>notify / profile_like</c>）。</summary>
    ProfileLike,

    /// <summary>输入状态（<c>notify / input_status</c>）。</summary>
    InputStatus,

    /// <summary>群精华消息变动（<c>essence</c>）。</summary>
    EssenceChanged,

    /// <summary>群消息表情回应（<c>group_msg_emoji_like</c>）。</summary>
    MessageEmojiLike,

    /// <summary>机器人离线（<c>bot_offline</c>）。</summary>
    BotOffline,

    /// <summary>加好友请求（<c>request / friend</c>）：只记录，不自动通过。</summary>
    FriendRequest,

    /// <summary>加群请求（<c>request / group</c>）：只记录，不自动通过。</summary>
    GroupRequest,
}
