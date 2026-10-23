using ZipperAIQQ.Agent.Security;

namespace ZipperAIQQ.Server.Commands;

/// <summary>
/// 内置命令清单（名字 / 中文名 / 代码里的默认最低角色）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是静态表而不是从 DI 里取 <c>IEnumerable&lt;ICommand&gt;</c>：
/// 策略对象在构造时要这份清单，而某些命令又依赖 Agent 池，池又依赖策略 ——
/// 用 DI 取会形成**循环依赖**（实测启动卡死）。
/// </para>
/// <para>
/// 新增命令时这里补一行；命令自己的 <c>MinRole</c> 仍是运行时默认值，
/// 两边不一致时以这里的登记为准（并在评审时核对）。
/// </para>
/// </remarks>
public static class CommandCatalog
{
    public static readonly IReadOnlyList<(string Name, string Label, Role MinRole)> All =
    [
        ("help", "帮助", Role.Normal),
        ("status", "状态", Role.Normal),
        ("persona", "人设", Role.Normal),
        ("remember", "记住", Role.Normal),
        ("search", "搜索历史", Role.Normal),
        ("compact", "压缩上下文", Role.Admin),
        ("reset", "重置会话", Role.Admin),
        ("forget", "忘记记忆", Role.Admin),
        ("export", "导出记录", Role.Admin),
        ("mode", "回复模式", Role.Admin),
        ("purge", "清空会话", Role.Owner),
        ("owner", "设置主人", Role.Owner),
        ("notice", "通知开关", Role.Admin),
    ];
}
