using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Agent.Security;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 工具目录（问题记录 #28）：名字 / 中文名 / 能力等级 / 默认角色 / 平台归属。
/// </summary>
/// <remarks>
/// <para>
/// 这份表是**策略层与工具实现之间的唯一约定**：BuildTools 按它过滤，
/// 客户端「工具管理」页按它展示。新增工具时两处都要加一行。
/// </para>
/// <para>
/// 没有能力要求的工具（发消息、查历史）默认 <see cref="Role.Normal"/>：
/// 它们本来就是所有人都能用的。
/// </para>
/// <para>
/// **平台归属**（决策五 / S3-D 的 D1）：只有真正"某个平台独有"的工具才标（QQ 的
/// 戳一戳 / 改签名 / NapCat API 查询），其余一律默认 <see cref="ToolPlatform.All"/>。
/// 发消息 / 表情 / 文件 / 定时 / 历史 / 网页搜索都是**平台无关**的 ——
/// 它们走统一抽象，不该因为换了平台就消失。
/// </para>
/// </remarks>
public static class ToolCatalog
{
    public static readonly IReadOnlyList<PolicyItem> All =
    [
        Item(SendMessageTool.ToolName, "发送消息", null),
        Item(ListFacesTool.ToolName, "查询表情", null),
        Item("file_list", "列目录", ToolCapability.ReadFile),
        Item("file_read", "读文件", ToolCapability.ReadFile),
        Item("file_write", "写文件", ToolCapability.WriteFile),
        Item("file_edit", "编辑文件", ToolCapability.WriteFile),
        Item("file_move", "移动文件", ToolCapability.WriteFile),
        Item("file_delete", "删除文件", ToolCapability.DeleteFile),
        Item("schedule_create", "创建定时任务", ToolCapability.Schedule),
        Item("schedule_list", "列出定时任务", ToolCapability.Schedule),
        Item("schedule_cancel", "取消定时任务", ToolCapability.Schedule),
        Item(MessageTools.SearchToolName, "检索本会话历史", null),
        Item(MessageTools.RecentToolName, "读取近期历史", null),
        Item(MessageTools.CrossSessionSearchToolName, "跨会话检索", ToolCapability.CrossSessionHistory),
        Item(MemoryRecallTool.ToolName, "跨会话语义召回", ToolCapability.CrossSessionHistory),
        Item(SendToOtherSessionTool.ToolName, "跨会话发消息", ToolCapability.SendToOtherSession),
        Item(CrossPlatformTools.ToolName, "跨平台通讯录", ToolCapability.QueryContacts),

        // ── 平台专用：这三样是 OneBot / NapCat 语义，别的平台根本不懂 ──
        Item(ContactTools.ToolName, "查询 NapCat API", null, ToolPlatform.Qq),
        Item(ProfileTools.SetSignatureToolName, "修改个性签名", ToolCapability.WriteProfile, ToolPlatform.Qq),
        Item(ProfileTools.PokeToolName, "戳一戳", ToolCapability.SendPoke, ToolPlatform.Qq),
        Item(Search.WebSearchTool.SearchToolName, "网页搜索", null),
        Item(Search.WebSearchTool.FetchToolName, "抓取网页", null),

        // ── 全家桶媒体（B2-b）：只读图 / 只写本地文件，默认 Normal ──
        Item(MediaTools.OcrToolName, "识别图片文字", ToolCapability.MediaRead),
        Item(MediaTools.TtsToolName, "文本转语音", ToolCapability.MediaSpeak),

        // ── 邮件双通道（B3）：读的是私人信件，默认管理员起（原文默认只给主人）──
        Item(MailTools.RenderToolName, "查看邮件画面", ToolCapability.MailRead),
        Item(MailTools.RawToolName, "读取邮件原文", ToolCapability.MailRaw),

        // ── 抖音专用（决策五 / S3-E）：只在抖音会话里对模型可见 ──
        Item(DouyinTools.FriendsToolName, "抖音好友", null, ToolPlatform.Douyin),
        Item(DouyinTools.GroupsToolName, "抖音群", null, ToolPlatform.Douyin),

        // ── 抖音视频理解（B4）：下整条视频 + 走 ASR，默认只给管理员 ──
        Item(VideoTools.VideoToolName, "看抖音视频画面", ToolCapability.MediaVideo, ToolPlatform.Douyin),
        Item(VideoTools.TranscriptToolName, "抖音视频转写", ToolCapability.MediaVideo, ToolPlatform.Douyin),

        // ── 抖音评论区（B5）：读评论是公开内容（默认管理员、硬底线白名单）──
        Item(CommentTools.ListToolName, "读抖音评论区", ToolCapability.CommentRead, ToolPlatform.Douyin),
    ];

    private static PolicyItem Item(
        string key,
        string label,
        ToolCapability? capability,
        ToolPlatform platforms = ToolPlatform.All)
    {
        var role = capability is null
            ? Role.Normal
            : PermissionGate.DefaultRoleOf(capability.Value);

        Role? floor = capability is null
            ? null
            : PermissionGate.HardFloorOf(capability.Value);

        return new PolicyItem(PolicyCategory.Tool, key, label, role, true, floor, platforms);
    }

    /// <summary>
    /// 按工具名查**平台归属**（未登记的工具返回 <see cref="ToolPlatform.All"/>）。
    /// </summary>
    /// <remarks>
    /// 未登记 → <c>All</c> 是**故意的**：新工具忘了登记时，宁可它对所有平台可见
    /// （最坏情况是模型看到一个在别处调不动的工具），也不要在所有平台上把它默默禁掉 ——
    /// 那种故障表现为"昨天还好好的功能今天突然不出现"，是最难查的一类。
    /// </remarks>
    public static ToolPlatform PlatformsOf(string toolName)
    {
        // 直接查表而不是再写一份 switch：单一口径，免得"表里标了 Qq、这里忘了改"
        foreach (var item in All)
        {
            if (string.Equals(item.Key, toolName, StringComparison.Ordinal))
            {
                return item.Platforms;
            }
        }

        return ToolPlatform.All;
    }

    /// <summary>该工具在指定平台（如 <c>qq</c> / <c>douyin</c>）的会话里是否可用。</summary>
    /// <remarks>
    /// 判定口径**只有一处**：<see cref="ToolPlatforms.IsAvailable"/> ——
    /// 通用工具处处可用（含未登记平台）、平台专用只在自己的平台出现、未登记平台只放行通用工具。
    /// 这里刻意不再补任何判断：两层判定留着迟早漂移，而漂移的方向是不可预测的
    /// （本层曾为 <c>All &amp; None == 0</c> 这个位运算坑临时补过一档，坑修到源头后就该撤掉）。
    /// </remarks>
    public static bool IsAvailableOn(string toolName, string? platform) =>
        ToolPlatforms.IsAvailable(PlatformsOf(toolName), platform);

    /// <summary>按工具名查能力等级（没有声明的返回 <c>null</c>）。</summary>
    public static ToolCapability? CapabilityOf(string toolName) => toolName switch
    {
        "file_list" or "file_read" => ToolCapability.ReadFile,
        "file_write" or "file_edit" or "file_move" => ToolCapability.WriteFile,
        "file_delete" => ToolCapability.DeleteFile,
        "schedule_create" or "schedule_list" or "schedule_cancel" => ToolCapability.Schedule,
        MessageTools.CrossSessionSearchToolName => ToolCapability.CrossSessionHistory,
        MemoryRecallTool.ToolName => ToolCapability.CrossSessionHistory,
        SendToOtherSessionTool.ToolName => ToolCapability.SendToOtherSession,
        ProfileTools.SetSignatureToolName => ToolCapability.WriteProfile,
        ProfileTools.PokeToolName => ToolCapability.SendPoke,
        CrossPlatformTools.ToolName => ToolCapability.QueryContacts,
        MediaTools.OcrToolName => ToolCapability.MediaRead,
        MediaTools.TtsToolName => ToolCapability.MediaSpeak,
        MailTools.RenderToolName => ToolCapability.MailRead,
        MailTools.RawToolName => ToolCapability.MailRaw,
        VideoTools.VideoToolName => ToolCapability.MediaVideo,
        VideoTools.TranscriptToolName => ToolCapability.MediaVideo,
        CommentTools.ListToolName => ToolCapability.CommentRead,
        _ => null,
    };
}
