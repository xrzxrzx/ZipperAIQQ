using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Memory;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage.Embeddings;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;
using ZipperAIQQ.Storage.Personas;

namespace ZipperAIQQ.Server.Commands;

/// <summary>命令列表（按角色过滤）。</summary>
/// <remarks>
/// ⚠️ 这里**不能**构造注入 <c>IEnumerable&lt;ICommand&gt;</c> ——
/// 它自己就是一条命令，会形成循环依赖（DI 会直接报 circular dependency）。
/// 改成执行时从容器取（延迟解析，构造期已过，不会循环）。
/// </remarks>
public sealed class HelpCommand : ICommand
{
    private readonly IServiceProvider _services;

    public HelpCommand(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public string Name => "help";
    public string Description => "列出可用命令";
    public Role MinRole => Role.Normal;

    public Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        var visible = _services.GetServices<ICommand>()
            .Where(c => context.Role >= c.MinRole)
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"可用命令（{visible.Count} 条，你当前是 {context.Role}）：");
        foreach (var command in visible)
        {
            sb.AppendLine($"- `/{command.Name}` — {command.Description}");
        }

        return Task.FromResult(sb.ToString().TrimEnd());
    }
}

/// <summary>状态：模型、会话消息数、调用指标。</summary>
public sealed class StatusCommand : ICommand
{
    private readonly IMetricsStore _metrics;
    private readonly IMessageStore _messages;
    private readonly ServerOptions _options;

    public StatusCommand(IMetricsStore metrics, IMessageStore messages, ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);

        _metrics = metrics;
        _messages = messages;
        _options = options;
    }

    public string Name => "status";
    public string Description => "模型 / token 用量 / 会话规模";
    public Role MinRole => Role.Normal;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        var count = await _messages.CountAsync(context.Chat.SessionId, ct).ConfigureAwait(false);
        var metrics = await _metrics.GetSummaryAsync(24, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine($"**状态** · 模型 `{_options.LlmModel}` · 本会话 {count} 条消息");
        sb.AppendLine();

        if (metrics.Count == 0)
        {
            sb.Append("近 24 小时：无调用记录");
            return sb.ToString();
        }

        var llm = metrics.FirstOrDefault(m => m.Kind == "llm.call");
        var retrieval = metrics.FirstOrDefault(m => m.Kind == "retrieval");
        var hit = metrics.FirstOrDefault(m => m.Kind == "retrieval.hit");
        var jev = metrics.FirstOrDefault(m => m.Kind == "jev.judge");
        var degrade = metrics.FirstOrDefault(m => m.Kind == "jev.degrade");
        var summary = metrics.FirstOrDefault(m => m.Kind == "summary");

        // 汇总表：一眼看清"花了多少、命中多少、该不该回"
        var hitRate = retrieval is { Count: > 0 }
            ? $"{(hit?.Count ?? 0) * 100.0 / retrieval.Count:F0}%（{retrieval.Count} 次检索 / {hit?.Count ?? 0} 次命中）"
            : "—";

        var jevText = jev is { Count: > 0 }
            ? $"{jev.Count} 次（回 {jev.TotalInputTokens} / 不回 {jev.Count - jev.TotalInputTokens}，降级 {degrade?.Count ?? 0}）"
            : "未启用";

        sb.AppendLine("| 指标 | 近 24 小时 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| LLM 调用 | {llm?.Count ?? 0} 次（平均 {llm?.AvgMs ?? 0:F0}ms） |");
        sb.AppendLine($"| Token | 输入 {llm?.TotalInputTokens ?? 0:N0} / 输出 {llm?.TotalOutputTokens ?? 0:N0} |");
        sb.AppendLine($"| 检索命中率 | {hitRate} |");
        sb.AppendLine($"| JEV 判断 | {jevText} |");
        sb.AppendLine($"| 上下文压缩 | {summary?.Count ?? 0} 次 |");
        sb.AppendLine();

        sb.AppendLine("| 调用类别 | 次数 | 平均耗时 |");
        sb.AppendLine("|---|---|---|");
        foreach (var m in metrics)
        {
            sb.AppendLine($"| {m.Kind} | {m.Count} | {m.AvgMs:F0} ms |");
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>查看 / 设置本会话人设。</summary>
public sealed class PersonaCommand : ICommand
{
    private readonly IPersonaStore _personas;

    public PersonaCommand(IPersonaStore personas)
    {
        ArgumentNullException.ThrowIfNull(personas);
        _personas = personas;
    }

    public string Name => "persona";
    public string Description => "查看人设；`/persona set <文本>` 设置本会话人设（管理员）";
    public Role MinRole => Role.Normal;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        if (args.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
        {
            // 写操作要管理员 —— 在类内再判一次：命令的最低角色是"所有人"，但 set 是管理员
            if (context.Role < Role.Admin)
            {
                return "权限不足：修改人设需要管理员。";
            }

            var content = args[4..].Trim();
            if (content.Length == 0)
            {
                return "用法：`/persona set <人设文本>`";
            }

            await _personas
                .SetAsync(context.Chat.SessionId, new Persona("custom", content), ct)
                .ConfigureAwait(false);

            return $"已设置本会话人设（{content.Length} 字）。下一轮生效。";
        }

        var persona = await _personas
            .GetAsync(context.Chat.Platform, context.Chat.Type.ToString(), context.Chat.Id, ct)
            .ConfigureAwait(false);

        var preview = persona.Content.Length <= 300 ? persona.Content : persona.Content[..300] + "…";
        return $"当前人设：{persona.Name}\n\n{preview}";
    }
}

/// <summary>写入一条长期记忆（作为消息落库 → 会被向量化、可被检索）。</summary>
public sealed class RememberCommand : ICommand
{
    private readonly IMessageStore _messages;

    public RememberCommand(IMessageStore messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages;
    }

    public string Name => "remember";
    public string Description => "写入长期记忆：`/remember <文本>`";
    public Role MinRole => Role.Normal;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return "用法：`/remember <要记住的内容>`";
        }

        // 存成一条普通消息（Sender=当前用户）：会走落库 → 向量补算 → 将来能被检索到
        var memory = new IncomingMessage
        {
            Chat = context.Chat,
            Sender = context.Sender,
            Segments = [new TextSegment($"[记忆] {args}")],
            OccurredAt = context.Now,
        };

        var id = await _messages.AppendAsync(memory, ct).ConfigureAwait(false);
        return $"已记住（消息 #{id}）。之后的检索能找回它。";
    }
}

/// <summary>检索本会话历史。</summary>
public sealed class SearchCommand : ICommand
{
    private readonly MemoryRetriever _retriever;

    public SearchCommand(MemoryRetriever retriever)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        _retriever = retriever;
    }

    public string Name => "search";
    public string Description => "检索历史：`/search <关键词>`";
    public Role MinRole => Role.Normal;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return "用法：`/search <关键词>`";
        }

        var hits = await _retriever
            .RetrieveAsync(context.Chat.SessionId, args, topK: 5, excludeRecentCount: 0, ct: ct)
            .ConfigureAwait(false);

        if (hits.Count == 0)
        {
            return "没有检索到相关内容。";
        }

        var sb = new StringBuilder($"命中 {hits.Count} 条：\n");
        foreach (var hit in hits)
        {
            var text = hit.Message.PlainText;
            if (text.Length > 80)
            {
                text = text[..80] + "…";
            }

            sb.AppendLine($"- #{hit.Id} [{BeijingTime.FormatShort(hit.Message.OccurredAt)}] {hit.Message.Sender.DisplayName}：{text}");
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>立即压缩一次摘要。</summary>
public sealed class CompactCommand : ICommand
{
    /// <summary>与 ChatAgentOptions 默认值一致；命令只触发一次压缩，不改热窗。</summary>
    private const int HotWindow = 20;

    private readonly SummaryService _summaries;

    public CompactCommand(SummaryService summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        _summaries = summaries;
    }

    public string Name => "compact";
    public string Description => "立即压缩上下文（管理员）";
    public Role MinRole => Role.Admin;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        var compressed = await _summaries
            .TryCompressAsync(context.Chat.SessionId, HotWindow, ct)
            .ConfigureAwait(false);

        return compressed
            ? "已压缩一轮（摘要已更新）。"
            : "当前没有到压缩阈值，什么都没做。";
    }
}

/// <summary>清空本会话上下文（日志保留）。</summary>
public sealed class ResetCommand : ICommand
{
    private readonly IChatAgentPool _agents;

    public ResetCommand(IChatAgentPool agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        _agents = agents;
    }

    public string Name => "reset";
    public string Description => "清空本会话上下文（日志保留，管理员）";
    public Role MinRole => Role.Admin;

    public Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        var agent = _agents.GetOrCreate(context.Chat);
        agent.ResetHistory();
        return Task.FromResult("已清空本会话上下文（聊天记录仍保留）。");
    }
}

/// <summary>删除一条长期记忆（向量）。</summary>
public sealed class ForgetCommand : ICommand
{
    private readonly IEmbeddingStore _embeddings;

    public ForgetCommand(IEmbeddingStore embeddings)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        _embeddings = embeddings;
    }

    public string Name => "forget";
    public string Description => "删除一条长期记忆：`/forget <消息 id>`（管理员）";
    public Role MinRole => Role.Admin;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        if (!long.TryParse(args.Trim(), out var id) || id <= 0)
        {
            return "用法：`/forget <消息 id>`（id 可从 `/search` 结果里看到）";
        }

        var removed = await _embeddings.DeleteAsync(id, ct).ConfigureAwait(false);
        return removed
            ? $"已删除消息 #{id} 的长期记忆（向量）。正文仍留在聊天记录里。"
            : $"消息 #{id} 没有对应的向量记录（可能从未向量化）。";
    }
}

/// <summary>导出本会话聊天记录（文本）。</summary>
public sealed class ExportCommand : ICommand
{
    private readonly IMessageStore _messages;

    public ExportCommand(IMessageStore messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages;
    }

    public string Name => "export";
    public string Description => "导出最近 50 条记录（管理员）";
    public Role MinRole => Role.Admin;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        var rows = await _messages.GetRecentAsync(context.Chat.SessionId, 50, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return "本会话没有记录。";
        }

        var sb = new StringBuilder($"最近 {rows.Count} 条：\n");
        foreach (var row in rows)
        {
            var who = row.IsCommand ? "[命令]" : row.IsOutgoing ? "AI" : row.Message.Sender.DisplayName;
            sb.AppendLine($"[{BeijingTime.Format(row.Message.OccurredAt)}] {who}：{row.Message.PlainText}");
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>删除本会话全部聊天记录（**不可恢复**）。</summary>
public sealed class PurgeCommand : ICommand
{
    private readonly IMessageStore _messages;

    public PurgeCommand(IMessageStore messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages;
    }

    public string Name => "purge";
    public string Description => "删除本会话全部记录（**不可恢复**，管理员）";
    public Role MinRole => Role.Admin;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        // 危险操作要显式确认：/purge confirm
        if (!string.Equals(args.Trim(), "confirm", StringComparison.OrdinalIgnoreCase))
        {
            return "这会删除本会话**全部**聊天记录且不可恢复。\n确认请发送 `/purge confirm`。";
        }

        var deleted = await _messages.PurgeAsync(context.Chat.SessionId, ct).ConfigureAwait(false);
        return $"已删除 {deleted} 条记录。";
    }
}

/// <summary>查看 / 切换回复触发模式（FR-11）。</summary>
public sealed class ModeCommand : ICommand
{
    private const string KeyPrefix = "replymode:";

    private readonly Storage.Settings.IRuntimeSettingsStore _settings;

    public ModeCommand(Storage.Settings.IRuntimeSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string Name => "mode";
    public string Description => "查看/切换回复模式：`/mode every|mention|jev`（管理员）";
    public Role MinRole => Role.Admin;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        var key = KeyPrefix + context.Chat.SessionId;

        if (string.IsNullOrWhiteSpace(args))
        {
            var current = await _settings.GetAsync(key, ct).ConfigureAwait(false);
            var shown = string.IsNullOrWhiteSpace(current) ? "（继承全局默认）" : current;
            return $"本会话回复模式：{shown}\n用法：`/mode every|mention|jev`";
        }

        if (!Enum.TryParse<Agent.ReplyMode>(args.Trim(), ignoreCase: true, out var mode))
        {
            return "不支持的模式。可选：`every`（每条都回）/ `mention`（仅 @ 时回）/ `jev`（判断模型决定）";
        }

        await _settings.SetAsync(key, mode.ToString(), ct).ConfigureAwait(false);
        return $"本会话回复模式已切换为 **{mode}**（下一轮生效）。";
    }
}
