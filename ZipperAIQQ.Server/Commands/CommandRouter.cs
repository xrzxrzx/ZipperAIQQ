using Serilog;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Server.Commands;

/// <summary>
/// 命令解析与执行（FR-9）。
/// </summary>
/// <remarks>
/// <para>
/// <b>匹配优先于模型（9.1）</b>：<c>/</c> 开头且命中命令 → 执行并回复，**不进 agent**。
/// </para>
/// <para>
/// <b>未命中按普通消息（9.2）</b>：<c>/foo</c> 不是命令时，原样交给模型，用户无感知。
/// </para>
/// <para>
/// <b>响应单独标注（9.4）</b>：命令响应以 <c>Direction=2</c> 落库，
/// 恢复历史时标成「[命令结果]」（user 侧），不会被当成 AI 自己说过的话。
/// </para>
/// </remarks>
public sealed class CommandRouter
{
    private readonly IReadOnlyDictionary<string, ICommand> _commands;
    private readonly IMessageChannel _channel;
    private readonly IMessageStore _store;
    private readonly ILogger _logger;

    public CommandRouter(IEnumerable<ICommand> commands, IMessageChannel channel, IMessageStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _commands = commands.ToDictionary(c => c.Name.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
        _channel = channel;
        _store = store;
        _logger = logger;
    }

    /// <summary>已注册命令（供 <c>/help</c> 使用）。</summary>
    public IReadOnlyList<ICommand> Commands => _commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

    /// <summary>解析 <c>/name args</c>；不是命令语法或命令不存在时返回 false。</summary>
    public bool TryResolve(string? text, out ICommand command, out string args)
    {
        command = null!;
        args = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '/')
        {
            return false;
        }

        var body = trimmed[1..].Trim();
        if (body.Length == 0)
        {
            return false;
        }

        var separator = body.IndexOfAny([' ', '\t', '\r', '\n']);
        var name = separator < 0 ? body : body[..separator];
        args = separator < 0 ? string.Empty : body[(separator + 1)..].Trim();

        return _commands.TryGetValue(name, out command!);
    }

    /// <summary>
    /// 命中命令就执行并回复，返回 <c>true</c> 表示「已处理，不要交模型」。
    /// </summary>
    public async Task<bool> TryExecuteAsync(IncomingMessage message, Role role, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!TryResolve(message.PlainText, out var command, out var args))
        {
            return false;
        }

        var context = new CommandContext(message.Chat, message.Sender, role, BeijingTime.Now);

        string response;
        if (role < command.MinRole)
        {
            // 命令语法已被识别，但角色不够：明确告诉用户，不回给模型瞎猜
            response = $"权限不足：`/{command.Name}` 需要 {command.MinRole} 及以上角色，当前是 {role}。";
        }
        else
        {
            try
            {
                response = await command.ExecuteAsync(context, args, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "命令 /{Name} 执行失败（会话 {SessionId}）", command.Name, message.Chat.SessionId);
                response = $"命令 `/{command.Name}` 执行失败：{ex.Message}";
            }
        }

        await ReplyAsync(message.Chat, response, ct).ConfigureAwait(false);
        _logger.Information("命令 /{Name} 已处理（会话 {SessionId}，角色 {Role}）",
            command.Name, message.Chat.SessionId, role);

        return true;
    }

    /// <summary>发出去 + 以「命令响应」身份落库（Direction=2）。</summary>
    private async Task ReplyAsync(ChatRef chat, string text, CancellationToken ct)
    {
        var outgoing = new OutgoingMessage
        {
            Chat = chat,
            Segments = [new TextSegment(text)],
        };

        await _channel.SendAsync(outgoing, ct).ConfigureAwait(false);

        try
        {
            await _store.AppendCommandAsync(outgoing, "system", BeijingTime.Now, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 已经发出去了，落库失败不改变事实
            _logger.Warning(ex, "命令响应落库失败（会话 {SessionId}）", chat.SessionId);
        }
    }
}
