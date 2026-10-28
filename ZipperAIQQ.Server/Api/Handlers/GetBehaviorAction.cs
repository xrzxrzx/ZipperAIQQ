using System.Text.Json;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 行为类设置（M2-E 第 14 项补齐）：回复模式 + 通知响应开关。
/// </summary>
/// <remarks>
/// <para>
/// 这些以前只能靠 QQ 命令改（<c>/mode</c>、<c>/notice</c>），客户端看不到也改不了 ——
/// 与「配置项要能在客户端配」的要求不符，这里补上读写。
/// </para>
/// <para>
/// **S3-D（决策六）起通知开关按平台**：键形如 <c>notice.reply:{platform}:{type}</c>
/// （见 <see cref="NoticeKey"/>）。读**新键优先**，读不到时**仅 qq** 回退单平台时代的老键
/// <c>notice.reply:{type}</c>（见 <see cref="LegacyNoticeKey"/>）—— 老配置不用迁移，也不会丢开关。
/// 写入由 <see cref="SetBehaviorAction"/> 负责：只写新键，qq 额外镜像一份老键，
/// 这样回退到旧版程序时开关不会突然"变空"。
/// </para>
/// <para>
/// 键的拼法与平台名规范化集中在本类：<see cref="SetBehaviorAction"/> 要用同一套。
/// 两处各写一遍迟早漂移（一边漏了回退、一边把 <c>Poke</c> 写成 <c>poke</c>）。
/// </para>
/// </remarks>
public sealed class GetBehaviorAction : IApiAction
{
    /// <summary>可配置回复模式的取值。</summary>
    public static readonly string[] ReplyModes = ["every", "mention", "jev"];

    /// <summary>
    /// 可响应（可配置开关）的通知类型（#34：从 6 类扩到全部非请求类；
    /// 2026-10-27 追加 4 个抖音语义类型，决策六）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **平台归属不在这里写**：由 <see cref="NoticeCatalog.PlatformOf"/> 决定，
    /// 每项的 <c>platform</c> 字段据此填 —— 清单里混着两个平台的类型是**故意**的：
    /// 行为页一次拿全，客户端按 <c>platform</c> 分 Tab。
    /// </para>
    /// <para>
    /// QQ 的 <c>FriendRequest</c> / <c>GroupRequest</c> 仍然**不在**本清单里：它们在
    /// <c>NoticeResponder</c> 里是硬编码"只记录不响应"。抖音的申请类类型不同 ——
    /// 它们**进清单、受开关控制**，默认关 = 同样只记录（见 <c>NoticeResponder</c> 的说明）。
    /// </para>
    /// </remarks>
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

        // ── 抖音语义（决策六 / S3-E）：排在末尾，客户端按 platform 分组 ──
        NoticeType.DouyinFriendRequest,
        NoticeType.DouyinGroupRequest,
        NoticeType.DouyinMemberChanged,
        NoticeType.DouyinSessionSynced,
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

    /// <summary>默认平台（缺省参数、老配置的归属 = <c>qq</c>）。</summary>
    /// <remarks>
    /// 不用 <c>const</c>：跨程序集的 <c>const</c> 会被**编译进调用方**，
    /// 哪天口径变了要全量重编才生效。
    /// </remarks>
    public static readonly string DefaultPlatform = NoticeCatalog.QqPlatform;

    /// <summary>
    /// 已知平台（目前只有 <see cref="NoticeCatalog"/> 里那两个）。
    /// </summary>
    /// <remarks>
    /// <see cref="NoticeCatalog"/> 没有暴露"平台清单"，所以在这里显式列出。
    /// ⚠️ 新增平台时**必须**同步这里，否则 <c>setBehavior</c> 会拒绝该平台。
    /// 不用 <c>ForPlatform(platform).Count &gt; 0</c> 推导：那是"有没有通知类型"，
    /// 而"是不是已知平台"是另一个问题 —— 两者混用会让"暂时没有通知类型的平台"
    /// 连配置都写不进去（抖音在 S3-E 入表前正是这种情况）。
    /// </remarks>
    public static readonly string[] KnownPlatforms =
        [NoticeCatalog.QqPlatform, NoticeCatalog.DouyinPlatform];

    private readonly IRuntimeSettingsStore _settings;

    public GetBehaviorAction(IRuntimeSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string Name => "getBehavior";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var requestedPlatform = NormalizePlatform(PayloadReader.GetString(payload, "platform"));
        var defaultMode = await _settings.GetAsync("replymode.default", ct).ConfigureAwait(false);

        var notices = new List<object>();
        foreach (var type in ReplyableNotices)
        {
            // 每一项归属它**自己的**平台（PlatformOf），与"本次查询的平台"无关：
            // 行为页要一次拿到全部平台的开关，靠每项的 platform 字段分组 ——
            // 否则切一次平台就得重查一遍，且"哪些类型属于哪个平台"的真相会有两个版本。
            var owner = NoticeCatalog.PlatformOf(type);
            var value = await ReadNoticeValueAsync(_settings, owner, type, ct).ConfigureAwait(false);

            notices.Add(new
            {
                type = type.ToString(),
                label = Describe(type),
                group = CommonNotices.Contains(type) ? "common" : "other",
                groupLabel = CommonNotices.Contains(type) ? "常用" : "其他",
                enabled = value is "1" or "true",
                platform = owner,
            });
        }

        return new
        {
            platform = requestedPlatform,

            // 平台下拉框 / 分组 Tab 的数据源：**有通知类型**的平台
            // （抖音的类型已入表，所以现在会返回 qq + douyin；以后加平台自动出现，不写死清单）
            platforms = KnownPlatforms
                .Where(platform => NoticeCatalog.ForPlatform(platform).Count > 0)
                .ToList(),

            replyMode = string.IsNullOrWhiteSpace(defaultMode) ? "every" : defaultMode.ToLowerInvariant(),
            replyModes = ReplyModes,
            notices,
        };
    }

    /// <summary>规范化平台名（空 → <see cref="DefaultPlatform"/>，统一小写）。</summary>
    public static string NormalizePlatform(string? platform) =>
        string.IsNullOrWhiteSpace(platform) ? DefaultPlatform : platform.Trim().ToLowerInvariant();

    /// <summary>是不是已知平台（入参先用 <see cref="NormalizePlatform"/> 规范化过）。</summary>
    public static bool IsKnownPlatform(string platform) =>
        KnownPlatforms.Contains(platform, StringComparer.Ordinal);

    /// <summary>某平台某通知类型的开关键：<c>notice.reply:{platform}:{type}</c>。</summary>
    public static string NoticeKey(string platform, NoticeType type) =>
        $"notice.reply:{NormalizePlatform(platform)}:{type}";

    /// <summary>单平台时代的老键：<c>notice.reply:{type}</c>（只读回退 + qq 镜像写）。</summary>
    public static string LegacyNoticeKey(NoticeType type) => $"notice.reply:{type}";

    /// <summary>这个平台是否要镜像写老键（只有 <see cref="NoticeCatalog.QqPlatform"/> 有老键）。</summary>
    public static bool HasLegacyKeys(string platform) =>
        string.Equals(NormalizePlatform(platform), NoticeCatalog.QqPlatform, StringComparison.Ordinal);

    /// <summary>读某平台某通知类型的开关值（新键优先，仅 qq 回退老键）。</summary>
    /// <remarks>
    /// 回退**不写回**：迁移是惰性的 —— 只要不写新键，老键就一直读得到，
    /// 用户不会因为一次升级被动改配置（与 S3-C 的 access 键同一口径）。
    /// </remarks>
    public static async Task<string?> ReadNoticeValueAsync(
        IRuntimeSettingsStore settings,
        string platform,
        NoticeType type,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var value = await settings.GetAsync(NoticeKey(platform, type), ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return HasLegacyKeys(platform)
            ? await settings.GetAsync(LegacyNoticeKey(type), ct).ConfigureAwait(false)
            : null;
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

        // 抖音语义（决策六 / S3-E）：中文名给客户端直接用，别再让前端自己翻译
        NoticeType.DouyinFriendRequest => "好友申请",
        NoticeType.DouyinGroupRequest => "入群申请",
        NoticeType.DouyinMemberChanged => "成员变化",
        NoticeType.DouyinSessionSynced => "会话同步",

        _ => type.ToString(),
    };
}
