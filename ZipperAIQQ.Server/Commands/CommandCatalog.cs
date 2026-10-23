using ZipperAIQQ.Agent.Security;

namespace ZipperAIQQ.Server.Commands;

/// <summary>
/// 内置命令清单（名字 + 代码里的默认最低角色）。
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
    public static readonly IReadOnlyList<(string Name, Role MinRole)> All =
    [
        ("help", Role.Normal),
        ("status", Role.Normal),
        ("persona", Role.Normal),
        ("remember", Role.Normal),
        ("search", Role.Normal),
        ("compact", Role.Admin),
        ("reset", Role.Admin),
        ("forget", Role.Admin),
        ("export", Role.Admin),
        ("mode", Role.Admin),
        ("purge", Role.Owner),
        ("owner", Role.Owner),
        ("notice", Role.Admin),
    ];
}
