using System.Text.Json;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 行为类设置（M2-E 第 14 项补齐）：回复模式 + 通知响应开关。
/// </summary>
/// <remarks>
/// 这些以前只能靠 QQ 命令改（<c>/mode</c>、<c>/notice</c>），客户端看不到也改不了 ——
/// 与「配置项要能在客户端配」的要求不符，这里补上读写。
/// </remarks>
public sealed class GetBehaviorAction : IApiAction
{
    /// <summary>可配置回复模式的取值。</summary>
    public static readonly string[] ReplyModes = ["every", "mention", "jev"];

    /// <summary>可响应（可配置开关）的通知类型（#34：从 6 类扩到全部非请求类）。</summary>
    public static readonly NoticeType[] ReplyableNotices =
    [
        NoticeType.Poke,
        NoticeType.GroupIncrease,
        NoticeType.GroupDecrease,
        NoticeType.GroupAdminChanged,
        NoticeType.GroupBan,
        NoticeType.MessageRecalled,
        NoticeType.FriendAdd,
        NoticeType.GroupFileUpload,
        NoticeType.GroupCardChanged,
        NoticeType.GroupNameChanged,
        NoticeType.GroupTitleChanged,
        NoticeType.GroupGrayTip,
        NoticeType.ProfileLike,
        NoticeType.InputStatus,
        NoticeType.EssenceChanged,
        NoticeType.MessageEmojiLike,
        NoticeType.BotOffline,
    ];

    /// <summary>「常用」的 6 类（界面上单独一组）。</summary>
    public static readonly NoticeType[] CommonNotices =
    [
        NoticeType.Poke,
        NoticeType.GroupIncrease,
        NoticeType.GroupDecrease,
        NoticeType.GroupAdminChanged,
        NoticeType.GroupBan,
        NoticeType.MessageRecalled,
    ];

    private readonly IRuntimeSettingsStore _settings;

    public GetBehaviorAction(IRuntimeSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string Name => "getBehavior";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var defaultMode = await _settings.GetAsync("replymode.default", ct).ConfigureAwait(false);

        var notices = new List<object>();
        foreach (var type in ReplyableNotices)
        {
            var value = await _settings.GetAsync("notice.reply:" + type, ct).ConfigureAwait(false);
            notices.Add(new
            {
                type = type.ToString(),
                label = Describe(type),
                group = CommonNotices.Contains(type) ? "common" : "other",
                groupLabel = CommonNotices.Contains(type) ? "常用" : "其他",
                enabled = value is "1" or "true",
            });
        }

        return new
        {
            replyMode = string.IsNullOrWhiteSpace(defaultMode) ? "every" : defaultMode.ToLowerInvariant(),
            replyModes = ReplyModes,
            notices,
        };
    }

    private static string Describe(NoticeType type) => type switch
    {
        NoticeType.Poke => "戳一戳",
        NoticeType.GroupIncrease => "入群",
        NoticeType.GroupDecrease => "退群",
        NoticeType.GroupAdminChanged => "管理员变动",
        NoticeType.GroupBan => "禁言",
        NoticeType.MessageRecalled => "撤回",
        NoticeType.FriendAdd => "好友添加",
        NoticeType.GroupFileUpload => "群文件",
        NoticeType.GroupCardChanged => "群名片",
        NoticeType.GroupNameChanged => "群名变更",
        NoticeType.GroupTitleChanged => "群头衔",
        NoticeType.GroupGrayTip => "群提示",
        NoticeType.ProfileLike => "资料点赞",
        NoticeType.InputStatus => "输入状态",
        NoticeType.EssenceChanged => "群精华",
        NoticeType.MessageEmojiLike => "表情回应",
        NoticeType.BotOffline => "机器人离线",
        _ => type.ToString(),
    };
}
