namespace ZipperAIQQ.Channel.Apis;

/// <summary>一个平台 API 的说明（问题记录 #28：NapCat API 类）。</summary>
public sealed record ChannelApiInfo(string Action, string Label, bool IsWrite);

/// <summary>
/// 平台 API 目录：**唯一清单**，白名单与客户端管理页都从这里来。
/// </summary>
/// <remarks>
/// 以前只读/写白名单分散在 NapCatChannel 里，客户端看不到也改不了；
/// 现在清单集中，通道按它校验，Server 按它生成策略项。
/// </remarks>
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

        // 写操作：只能经由带门禁的专用工具调用，不开放给通用查询
        new("set_self_longnick", "修改个性签名", true),
        new("send_poke", "戳一戳", true),
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
