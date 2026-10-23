namespace ZipperAIQQ.Channel.Apis;

/// <summary>一个平台 API 的说明（问题记录 #28：NapCat API 类）。</summary>
/// <param name="Action">OneBot action 名。</param>
/// <param name="Label">中文名。</param>
/// <param name="IsWrite">写操作（只能由带门禁的专用工具调用）。</param>
/// <param name="ShowInCatalog">
/// 是否出现在「工具管理」页。写操作已有对应的专用工具（poke_someone / set_my_signature），
/// 再单列一条会让人以为有两套入口（用户反馈：戳一戳与改签名重复）。
/// </param>
public sealed record ChannelApiInfo(string Action, string Label, bool IsWrite, bool ShowInCatalog = true);

/// <summary>
/// 平台 API 目录：**唯一清单**，白名单与客户端管理页都从这里来。
/// </summary>
public static class ChannelApiCatalog
{
    public static readonly IReadOnlyList<ChannelApiInfo> All =
    [
        new("get_friend_list", "好友列表", false),
        new("get_group_list", "群列表", false),
        new("get_group_info", "群信息", false),
        new("get_group_member_list", "群成员列表", false),
        new("get_group_member_info", "单个群成员", false),
        new("get_stranger_info", "陌生人资料", false),
        new("get_login_info", "登录信息", false),
        new("get_version_info", "版本信息", false),

        // 写操作：只能经由带门禁的专用工具调用，且不在管理页重复展示
        new("set_self_longnick", "修改个性签名", true, ShowInCatalog: false),
        new("send_poke", "戳一戳", true, ShowInCatalog: false),
    ];

    public static IReadOnlySet<string> ReadActions { get; } = All
        .Where(a => !a.IsWrite)
        .Select(a => a.Action)
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> WriteActions { get; } = All
        .Where(a => a.IsWrite)
        .Select(a => a.Action)
        .ToHashSet(StringComparer.Ordinal);
}
