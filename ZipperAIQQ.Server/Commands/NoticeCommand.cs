using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Commands;

/// <summary>查看 / 开关通知响应（FR-18.3）。</summary>
/// <remarks>
/// 默认**全部关闭**（只记录不响应）；五类通知逐个可开，避免群里被通知刷屏。
/// </remarks>
public sealed class NoticeCommand : ICommand
{
    private const string KeyPrefix = "notice.reply:";

    private static readonly NoticeType[] Replyable =
    [
        NoticeType.Poke,
        NoticeType.GroupIncrease,
        NoticeType.GroupDecrease,
        NoticeType.GroupAdminChanged,
        NoticeType.GroupBan,
        NoticeType.MessageRecalled,
    ];

    private readonly IRuntimeSettingsStore _settings;

    public NoticeCommand(IRuntimeSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string Name => "notice";
    public string Description => "查看/开关通知响应：`/notice on|off <类型>`（管理员）";
    public Role MinRole => Role.Admin;

    public async Task<string> ExecuteAsync(CommandContext context, string args, CancellationToken ct = default)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            var lines = new List<string> { "通知响应状态（默认全关）：" };
            foreach (var type in Replyable)
            {
                var value = await _settings.GetAsync(KeyPrefix + type, ct).ConfigureAwait(false);
                lines.Add($"- {type}：{(value is "1" or "true" ? "开" : "关")}");
            }

            lines.Add("用法：`/notice on|off poke`（可选：" + string.Join(" / ", Replyable) + "）");
            return string.Join('\n', lines);
        }

        if (parts.Length < 2)
        {
            return "用法：`/notice on|off <类型>`";
        }

        var action = parts[0].ToLowerInvariant();
        if (action is not ("on" or "off"))
        {
            return "第一个参数只能是 `on` 或 `off`。";
        }

        if (!Enum.TryParse<NoticeType>(parts[1], ignoreCase: true, out var noticeType) ||
            !Replyable.Contains(noticeType))
        {
            return $"不支持的通知类型：{parts[1]}。可选：{string.Join(" / ", Replyable)}";
        }

        await _settings.SetAsync(KeyPrefix + noticeType, action == "on" ? "1" : "0", ct).ConfigureAwait(false);
        return $"已{(action == "on" ? "开启" : "关闭")} {noticeType} 的通知响应（下一轮生效）。";
    }
}
