using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Agent.Security;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 工具目录（问题记录 #28）：名字 / 中文名 / 能力等级 / 默认角色。
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
        Item(SendToOtherSessionTool.ToolName, "跨会话发消息", ToolCapability.SendToOtherSession),
        Item(ContactTools.ToolName, "查询 NapCat API", null),
        Item(ProfileTools.SetSignatureToolName, "修改个性签名", ToolCapability.WriteProfile),
        Item(ProfileTools.PokeToolName, "戳一戳", ToolCapability.SendPoke),
        Item(Search.WebSearchTool.SearchToolName, "网页搜索", null),
        Item(Search.WebSearchTool.FetchToolName, "抓取网页", null),
    ];

    private static PolicyItem Item(string key, string label, ToolCapability? capability)
    {
        var role = capability is null
            ? Role.Normal
            : PermissionGate.DefaultRoleOf(capability.Value);

        Role? floor = capability is null
            ? null
            : PermissionGate.HardFloorOf(capability.Value);

        return new PolicyItem(PolicyCategory.Tool, key, label, role, true, floor);
    }

    /// <summary>按工具名查能力等级（没有声明的返回 <c>null</c>）。</summary>
    public static ToolCapability? CapabilityOf(string toolName) => toolName switch
    {
        "file_list" or "file_read" => ToolCapability.ReadFile,
        "file_write" or "file_edit" or "file_move" => ToolCapability.WriteFile,
        "file_delete" => ToolCapability.DeleteFile,
        "schedule_create" or "schedule_list" or "schedule_cancel" => ToolCapability.Schedule,
        MessageTools.CrossSessionSearchToolName => ToolCapability.CrossSessionHistory,
        SendToOtherSessionTool.ToolName => ToolCapability.SendToOtherSession,
        ProfileTools.SetSignatureToolName => ToolCapability.WriteProfile,
        ProfileTools.PokeToolName => ToolCapability.SendPoke,
        _ => null,
    };
}
