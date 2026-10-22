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

    /// <summary>首批支持响应的通知类型（与 <c>/notice</c> 命令保持一致）。</summary>
    public static readonly NoticeType[] ReplyableNotices =
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
        _ => type.ToString(),
    };
}
