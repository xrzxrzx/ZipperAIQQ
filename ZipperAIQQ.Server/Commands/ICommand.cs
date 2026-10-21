using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Server.Commands;

/// <summary>一次命令调用需要的上下文（由路由注入，模型碰不到）。</summary>
public sealed record CommandContext(
    ChatRef Chat,
    SenderInfo Sender,
    Role Role,
    DateTimeOffset Now);

/// <summary>
/// 一条 <c>/</c> 命令（FR-9）。
/// </summary>
/// <remarks>
/// <para>
/// <b>可扩展性（9.5）</b>：新增命令 = 新写一个类 + 在容器里加一行注册，路由不用改。
/// </para>
/// <para>
/// <b>命名</b>：<see cref="Name"/> 不含 <c>/</c>、大小写不敏感。
/// </para>
/// </remarks>
public interface ICommand
{
    /// <summary>命令名（不含 <c>/</c>），如 <c>help</c>。</summary>
    string Name { get; }

    /// <summary>一句话说明（用在 <c>/help</c> 里）。</summary>
    string Description { get; }

    /// <summary>最低可用角色（FR-9.6）。</summary>
    Role MinRole { get; }

    /// <summary>
    /// 执行并返回**要发回聊天**的文本。
    /// </summary>
    /// <param name="args">命令名之后的原始参数（可能为空）。</param>
    Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default);
}
