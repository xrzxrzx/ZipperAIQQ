using System.Globalization;
using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Schedules;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 定时任务工具（FR-14）：创建 / 列表 / 取消。
/// </summary>
/// <remarks>
/// <para>
/// 任务**持久化在数据库**（数据库即队列），重启不丢；执行由服务端 1 分钟轮询驱动。
/// </para>
/// <para>
/// 时间参数一律**北京时间**（FR-15.3），格式 <c>yyyy-MM-dd HH:mm</c>；
/// 存库时转 UTC 毫秒。
/// </para>
/// </remarks>
public static class ScheduleTools
{
    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm",
    ];

    public static IReadOnlyList<AITool> Create(
        ToolCallContext context,
        IPermissionGate gate,
        IScheduleStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(store);

        if (!gate.IsAllowed(ToolCapability.Schedule, context.Role))
        {
            return [];
        }

        return
        [
            CreateCreate(context, gate, store),
            CreateList(context, gate, store),
            CreateCancel(context, gate, store),
        ];
    }

    private static AIFunction CreateCreate(ToolCallContext context, IPermissionGate gate, IScheduleStore store)
    {
        return AIFunctionFactory.Create(
            async (string when, string content, string? mentionUser, int? repeatMinutes, string? target) =>
            {
                if (Deny(gate, context) is { } denied)
                {
                    return denied;
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    return "要发送的内容不能为空。";
                }

                if (!TryParseBeijing(when, out var at))
                {
                    return "时间格式不对：请用 `yyyy-MM-dd HH:mm`（**北京时间**），例如 `2026-10-21 23:30`。";
                }

                var now = DateTimeOffset.UtcNow;
                if (at <= now.AddMinutes(-1))
                {
                    return $"这个时间已经过去了（{BeijingTime.Format(at)}）。请给一个将来时间。";
                }

                var repeat = repeatMinutes is > 0;
                if (repeat && repeatMinutes < 1)
                {
                    return "重复间隔至少 1 分钟。";
                }

                // 目标会话默认当前会话；发给别人需要跨会话能力（管理员/主人）
                var chat = context.Chat;
                if (!string.IsNullOrWhiteSpace(target))
                {
                    if (!SessionRefParser.TryParse(target, context.Chat.Platform, out var parsed, out var parseError))
                    {
                        return parseError!;
                    }

                    if (!string.Equals(parsed.SessionId, context.Chat.SessionId, StringComparison.Ordinal) &&
                        !gate.IsAllowed(ToolCapability.SendToOtherSession, context.Role))
                    {
                        return "只有管理员/主人才能把定时消息发给其他会话。";
                    }

                    chat = parsed;
                }

                var task = new ScheduledTask(
                    Id: 0,
                    Chat: chat,
                    Content: content.Trim(),
                    MentionUserId: string.IsNullOrWhiteSpace(mentionUser) ? null : mentionUser.Trim(),
                    Repeat: repeat ? ScheduleRepeat.Interval : ScheduleRepeat.Once,
                    IntervalMinutes: repeat ? repeatMinutes!.Value : 0,
                    NextRunAt: at,
                    CreatedAt: now,
                    CreatedBy: context.Sender.UserId,
                    Status: ScheduleStatus.Pending);

                var id = await store.CreateAsync(task).ConfigureAwait(false);

                var kind = repeat ? $"每 {repeatMinutes} 分钟" : "一次性";
                var mention = task.MentionUserId is null ? string.Empty : $"，@ {task.MentionUserId}";
                var where = string.Equals(chat.SessionId, context.Chat.SessionId, StringComparison.Ordinal)
                    ? "当前会话"
                    : chat.SessionId;
                return $"已创建定时任务 #{id}：{BeijingTime.Format(at)}（{kind}{mention}），发到{where}。到点会自动发送。";
            },
            name: "schedule_create",
            description:
                "创建一个定时任务：到指定时间（**北京时间**，`yyyy-MM-dd HH:mm`）自动发一条消息。" +
                "默认发到**当前会话**；`target` 可指定 `qq:p<QQ号>`（私聊）或 `qq:g<群号>`（群，需管理员/主人）。" +
                "可选 repeatMinutes 表示每隔多少分钟重复一次；可选 mentionUser 在群聊里 @ 某人。");
    }

    private static AIFunction CreateList(ToolCallContext context, IPermissionGate gate, IScheduleStore store)
    {
        return AIFunctionFactory.Create(
            async () =>
            {
                if (Deny(gate, context) is { } denied)
                {
                    return denied;
                }

                var tasks = await store.ListAsync(context.Chat.SessionId).ConfigureAwait(false);
                if (tasks.Count == 0)
                {
                    return "当前会话没有定时任务。";
                }

                var lines = new List<string> { $"当前会话共 {tasks.Count} 个定时任务：" };
                foreach (var task in tasks)
                {
                    var status = task.Status switch
                    {
                        ScheduleStatus.Pending => "待执行",
                        ScheduleStatus.Done => "已完成",
                        _ => "已取消",
                    };

                    var kind = task.Repeat == ScheduleRepeat.Interval ? $"每 {task.IntervalMinutes} 分钟" : "一次性";
                    lines.Add($"- #{task.Id} {status}：{BeijingTime.Format(task.NextRunAt)}（{kind}）{Truncate(task.Content, 40)}");
                }

                return string.Join('\n', lines);
            },
            name: "schedule_list",
            description: "列出**当前会话**的所有定时任务（含已完成的），用于确认或找 id。");
    }

    private static AIFunction CreateCancel(ToolCallContext context, IPermissionGate gate, IScheduleStore store)
    {
        return AIFunctionFactory.Create(
            async (long id) =>
            {
                if (Deny(gate, context) is { } denied)
                {
                    return denied;
                }

                if (id <= 0)
                {
                    return "任务 id 必须是正整数。";
                }

                var cancelled = await store.CancelAsync(id, context.Chat.SessionId).ConfigureAwait(false);
                return cancelled
                    ? $"已取消定时任务 #{id}。"
                    : $"没有找到可取消的任务 #{id}（可能不存在、已执行完，或不属于本会话）。";
            },
            name: "schedule_cancel",
            description: "取消**当前会话**里某个待执行的定时任务（需要任务 id，可先用 schedule_list 查）。");
    }

    private static string? Deny(IPermissionGate gate, ToolCallContext context)
    {
        var decision = gate.Check(ToolCapability.Schedule, context);
        return decision.Allowed ? null : decision.Reason;
    }

    private static bool TryParseBeijing(string text, out DateTimeOffset result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(text) ||
            !DateTime.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            return false;
        }

        // 输入按北京时间解释，转成带 +08:00 偏移的时刻
        result = new DateTimeOffset(local, TimeSpan.FromHours(8));
        return true;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");
}
