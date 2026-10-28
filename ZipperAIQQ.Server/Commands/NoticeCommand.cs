using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Commands;

/// <summary>查看 / 开关通知响应（FR-18.3）。</summary>
/// <remarks>
/// 默认**全部关闭**（只记录不响应）；五类通知逐个可开，避免群里被通知刷屏。
/// </remarks>
public sealed class NoticeCommand : ICommand
{
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
            // 按**本会话所在平台**读（S3-D）：命令在哪个平台发，就报哪个平台的开关
            var statusPlatform = GetBehaviorAction.NormalizePlatform(context.Chat.Platform);
            var statusLines = new List<string> { $"通知响应状态（{statusPlatform}，默认全关）：" };
            foreach (var type in Replyable)
            {
                var current = await GetBehaviorAction
                    .ReadNoticeValueAsync(_settings, statusPlatform, type, ct)
                    .ConfigureAwait(false);
                statusLines.Add($"- {type}：{(current is "1" or "true" ? "开" : "关")}");
            }

            statusLines.Add("用法：`/notice on|off poke`（可选：" + string.Join(" / ", Replyable) + "）");
            return string.Join('\n', statusLines);
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

        // ⚠️ 必须写**平台键**（S3-D / 决策六）：老写法只写 `notice.reply:{type}`，
        // 一旦客户端保存过 `notice.reply:qq:{type}`，/notice 写老键就会出现
        // 「命令说开了、行为页显示关」的读写分歧 —— 平台化把它变成了真 bug，这里修掉。
        var targetPlatform = GetBehaviorAction.NormalizePlatform(context.Chat.Platform);
        var flag = action == "on" ? "1" : "0";

        await _settings
            .SetAsync(GetBehaviorAction.NoticeKey(targetPlatform, noticeType), flag, ct)
            .ConfigureAwait(false);

        if (GetBehaviorAction.HasLegacyKeys(targetPlatform))
        {
            // qq 镜像写老键：回退到旧版程序时开关不会丢
            await _settings
                .SetAsync(GetBehaviorAction.LegacyNoticeKey(noticeType), flag, ct)
                .ConfigureAwait(false);
        }

        return $"已{(action == "on" ? "开启" : "关闭")} {noticeType} 的通知响应（{targetPlatform}，下一轮生效）。";
    }
}
