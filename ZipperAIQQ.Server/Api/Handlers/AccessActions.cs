using System.Text.Json;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Server.Access;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 人员与名单配置（用户长期要求：管理员 / 主人 / 黑白名单都要能在客户端配）。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-23 起名单模型改为**黑白两份并存 + 同条目互斥**（问题记录 #20）。
/// </para>
/// <para>
/// **S3-C（决策四）起按平台**：入参 <c>platform</c>（缺省 <c>qq</c>，兼容旧客户端），
/// 键统一走 <see cref="AccessKeys"/>，旧键只读回退（见 <see cref="AccessSettings"/>）。
/// 候选人也按平台取（<see cref="IContactDirectoryRegistry"/>）—— 抖音的好友不能拿来当 QQ 的主人。
/// </para>
/// <para>
/// 主人放行范围（私聊 + 主人所在的群）不在这里写 —— 由 <c>OwnerScopeResolver</c> 计算后
/// 写入过滤器，本类只负责读出来展示。
/// </para>
/// </remarks>
public sealed class GetAccessAction : IApiAction
{
    private readonly IRuntimeSettingsStore _settings;
    private readonly IRoleResolver _roles;
    private readonly IChatFilter _filter;
    private readonly IContactDirectoryRegistry _directories;
    private readonly Serilog.ILogger _logger;

    public GetAccessAction(
        IRuntimeSettingsStore settings,
        IRoleResolver roles,
        IChatFilter filter,
        IContactDirectoryRegistry directories,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _roles = roles;
        _filter = filter;
        _directories = directories;
        _logger = logger;
    }

    public string Name => "getAccess";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var platform = AccessKeys.Normalize(PayloadReader.GetString(payload, "platform"));

        var ownerId = await AccessSettings.GetOwnerAsync(_settings, platform, ct).ConfigureAwait(false);

        var admins = (await AccessSettings.GetAdminsAsync(_settings, platform, ct).ConfigureAwait(false)).ToList();
        if (admins.Count == 0 && _roles is ConfiguredRoleResolver resolver)
        {
            // 库里没写就展示启动配置里的那份（环境变量 / YAML 的 admins）
            admins = resolver.Admins(platform).ToList();
        }

        var (whitelist, blacklist) = await AccessSettings
            .GetListsAsync(_settings, platform, ct).ConfigureAwait(false);

        // 候选人：该平台的联系人目录（抖音未接入时为空，不是"假装没有好友"）
        var directory = _directories.For(platform);
        IReadOnlyList<Channel.ContactInfo> friends = [];
        IReadOnlyList<Channel.GroupInfo> groups = [];
        if (directory is not null)
        {
            try
            {
                friends = await directory.GetFriendsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "读取 {Platform} 好友列表失败，候选人置空", platform);
                friends = [];
            }

            try
            {
                groups = await directory.GetGroupsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "读取 {Platform} 群列表失败，候选人置空", platform);
                groups = [];
            }
        }

        return new
        {
            platform,
            // 平台下拉框的数据源：有联系人目录 = 人员可配的平台（**邮件不在此列**，它只收不回）
            platforms = _directories.Platforms,

            ownerId,
            admins,

            whitelist = whitelist.ToList(),
            blacklist = blacklist.ToList(),
            alwaysAllow = _filter.AlwaysAllow(platform),

            // 旧字段（B4 之前的老客户端还在用）：按「白名单优先」折算成单份
            filterMode = whitelist.Count > 0
                ? nameof(ChatListMode.Whitelist)
                : nameof(ChatListMode.Blacklist),
            filterEntries = whitelist.Count > 0 ? whitelist.ToList() : blacklist.ToList(),
            filterCount = whitelist.Count + blacklist.Count,

            candidates = friends
                .Select(f => new
                {
                    userId = f.UserId,
                    nickname = string.IsNullOrWhiteSpace(f.Remark) ? f.Nickname : $"{f.Nickname}（{f.Remark}）",
                })
                .ToList(),

            groupCandidates = groups
                .Select(g => new
                {
                    groupId = g.GroupId,
                    name = g.Name,
                    sessionId = $"{platform}:g{g.GroupId}",
                })
                .ToList(),
        };
    }
}

/// <summary>
/// 改写人员与名单（第 14 项延伸）：**写库 + 立即生效**。
/// </summary>
/// <remarks>
/// <para>
/// payload：<c>{ "platform": "qq", "admins": ["123"], "whitelist": ["qq:p123"], "blacklist": ["qq:g456"] }</c>，
/// 都可单独给；<c>platform</c> 缺省 <c>qq</c>。
/// </para>
/// <para>
/// 旧 payload（<c>filterMode</c> + <c>filterEntries</c>）继续接受：折算成该平台的单份名单，另一份清空。
/// </para>
/// <para>
/// <b>管理员 × 黑名单冲突</b>（问题记录 #23）：首次提交若发现管理员在黑名单里，返回 <c>409</c>
/// 并列出冲突账号；带 <c>confirmDemoteAdmins: true</c> 重发则**剥夺管理员并拉黑**。
/// 冲突判定**只看同一平台**：QQ 拉黑某人，不该影响他在抖音的管理员身份。
/// </para>
/// </remarks>
public sealed class SetAccessAction : IApiAction
{
    private readonly IRuntimeSettingsStore _settings;
    private readonly IRoleResolver _roles;
    private readonly IChatFilter _filter;
    private readonly IConfigStore _configs;

    public SetAccessAction(
        IRuntimeSettingsStore settings,
        IRoleResolver roles,
        IChatFilter filter,
        IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(configs);

        _settings = settings;
        _roles = roles;
        _filter = filter;
        _configs = configs;
    }

    public string Name => "setAccess";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 必须是对象。");
        }

        var platform = AccessKeys.Normalize(PayloadReader.GetString(payload, "platform"));
        var changed = new List<string>();

        // ── ① 管理员 ──
        List<string>? admins = null;
        if (payload.TryGetProperty("admins", out var adminsElement))
        {
            admins = ReadStringArray(adminsElement, "admins").Distinct(StringComparer.Ordinal).ToList();
        }

        // ── ② 名单：新字段优先，旧字段折算 ──
        var whitelistInput = payload.TryGetProperty("whitelist", out var whitelistElement)
            ? ReadStringArray(whitelistElement, "whitelist")
            : null;
        var blacklistInput = payload.TryGetProperty("blacklist", out var blacklistElement)
            ? ReadStringArray(blacklistElement, "blacklist")
            : null;

        string? legacyMode = null;
        if (payload.TryGetProperty("filterMode", out var modeElement))
        {
            if (modeElement.ValueKind != JsonValueKind.String)
            {
                throw new ApiException(ApiResponse.BadRequest, "filterMode 必须是字符串。");
            }

            legacyMode = ParseMode(modeElement.GetString());
        }

        var legacyEntries = payload.TryGetProperty("filterEntries", out var entriesElement)
            ? ReadStringArray(entriesElement, "filterEntries")
            : null;

        // 有没有提交"可识别字段"：决定末尾"什么都没写"时是 400（垃圾 payload）还是 skipped（没变化）
        var sawKnownField = admins is not null
            || whitelistInput is not null
            || blacklistInput is not null
            || legacyMode is not null
            || legacyEntries is not null;

        if (legacyMode is not null || legacyEntries is not null)
        {
            if (whitelistInput is not null || blacklistInput is not null)
            {
                throw new ApiException(ApiResponse.BadRequest,
                    "filterMode / filterEntries 是旧字段，不能与 whitelist / blacklist 同时提交。");
            }

            var mode = legacyMode
                ?? (_filter.WhitelistCount(platform) > 0
                    ? nameof(ChatListMode.Whitelist)
                    : nameof(ChatListMode.Blacklist));
            var entries = legacyEntries
                ?? (mode == nameof(ChatListMode.Whitelist)
                    ? _filter.Whitelist(platform).ToList()
                    : _filter.Blacklist(platform).ToList());

            // 旧语义是「单模式 + 单名单」：写进对应那份，另一份清空
            if (mode == nameof(ChatListMode.Whitelist))
            {
                whitelistInput = entries;
                blacklistInput = [];
            }
            else
            {
                blacklistInput = entries;
                whitelistInput = [];
            }
        }

        var nextWhitelist = whitelistInput ?? _filter.Whitelist(platform).ToList();
        var nextBlacklist = blacklistInput ?? _filter.Blacklist(platform).ToList();

        var overlap = nextWhitelist
            .Intersect(nextBlacklist, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (overlap.Count > 0)
        {
            throw new ApiException(ApiResponse.BadRequest,
                $"同一条目不能同时在白名单与黑名单：{string.Join("、", overlap)}");
        }

        // ── ③ 管理员 × 黑名单冲突（#23，仅同平台）──
        if (admins is not null)
        {
            var conflicts = admins
                .Where(a => nextBlacklist.Any(e => IsPrivateSessionOf(platform, e, a)))
                .ToList();

            if (conflicts.Count > 0)
            {
                var confirm = payload.TryGetProperty("confirmDemoteAdmins", out var confirmElement)
                    && confirmElement.ValueKind == JsonValueKind.True;

                if (!confirm)
                {
                    throw new ApiException(
                        ApiResponse.Conflict,
                        $"以下管理员在黑名单里：{string.Join("、", conflicts)}。" +
                        "确认后将剥夺其管理员身份并拉黑；请带 confirmDemoteAdmins=true 重发。",
                        new { code = "admin_in_blacklist", admins = conflicts, platform });
                }

                admins = admins.Except(conflicts, StringComparer.OrdinalIgnoreCase).ToList();
                await PersistAdminsAsync(platform, admins, ct).ConfigureAwait(false);

                await _configs
                    .AppendAuditAsync("demoteAdmin", "http-client", string.Join(",", conflicts), ct)
                    .ConfigureAwait(false);

                changed.Add($"demoteAdmins={conflicts.Count}");
            }
            else
            {
                // #10：与库里当前管理员一致 → 不写、不审计（但不要提前返回：名单可能还在后面要写）
                var currentAdmins = (await AccessSettings
                    .GetAdminsAsync(_settings, platform, ct).ConfigureAwait(false)).ToList();

                if (currentAdmins.Count == 0 && _roles is ConfiguredRoleResolver resolver)
                {
                    currentAdmins = resolver.Admins(platform).ToList();
                }

                if (!SameSet(admins, currentAdmins))
                {
                    await PersistAdminsAsync(platform, admins, ct).ConfigureAwait(false);
                    changed.Add($"admins={admins.Count}");
                }
            }
        }

        // ── ④ 写名单 + 立即生效 ──
        if (whitelistInput is not null || blacklistInput is not null)
        {
            // #10：运行中的名单与提交值一致 → 跳过（列表没变就不该每次写审计）
            var listsUnchanged =
                SameSet(nextWhitelist, _filter.Whitelist(platform)) &&
                SameSet(nextBlacklist, _filter.Blacklist(platform));

            if (listsUnchanged)
            {
                if (changed.Count == 0)
                {
                    return new { updated = 0, skipped = true, changed, platform };
                }
            }
            else
            {
                await _settings.SetAsync(
                    AccessKeys.Whitelist(platform), string.Join(',', nextWhitelist), ct).ConfigureAwait(false);
                await _settings.SetAsync(
                    AccessKeys.Blacklist(platform), string.Join(',', nextBlacklist), ct).ConfigureAwait(false);

                // 旧键处理（仅 qq）：
                //  · 名单**镜像写**旧的 access.whitelist / blacklist —— 回退到旧版程序时名单不凭空消失，
                //    且"用户显式清空"也能被旧键如实表达（旧键同样写成空串，不会把更旧的条目复活）。
                //  · 更旧的「模式 + 单名单」（filterMode / filterEntries）必须**清空**：
                //    否则两份新键都空时，读取会回退到那对旧键，看起来像"名单自己变回来了"。
                if (AccessKeys.HasLegacyKeys(platform))
                {
                    await _settings.SetAsync(
                        AccessKeys.LegacyWhitelistKey, string.Join(',', nextWhitelist), ct).ConfigureAwait(false);
                    await _settings.SetAsync(
                        AccessKeys.LegacyBlacklistKey, string.Join(',', nextBlacklist), ct).ConfigureAwait(false);
                    await _settings.SetAsync(AccessKeys.LegacyFilterModeKey, string.Empty, ct).ConfigureAwait(false);
                    await _settings.SetAsync(AccessKeys.LegacyFilterEntriesKey, string.Empty, ct).ConfigureAwait(false);
                }

                _filter.Update(platform, new ChatFilterOptions
                {
                    Whitelist = nextWhitelist,
                    Blacklist = nextBlacklist,
                    AlwaysAllow = _filter.AlwaysAllow(platform).ToList(),
                });

                changed.Add($"whitelist={nextWhitelist.Count}/blacklist={nextBlacklist.Count}");
            }
        }

        if (changed.Count == 0)
        {
            // 提交了可识别字段但值没变化（例如客户端把管理员原样再存一次）→ **不是错误**，
            // 报 200 + skipped。只有"一个可识别字段都没带"才是真的坏 payload。
            if (sawKnownField)
            {
                return new { updated = 0, skipped = true, changed, platform };
            }

            throw new ApiException(ApiResponse.BadRequest,
                "没有可写入的字段（admins / whitelist / blacklist / filterMode / filterEntries）。");
        }

        await _configs
            .AppendAuditAsync("setAccess", "http-client", $"{platform}:{string.Join(",", changed)}", ct)
            .ConfigureAwait(false);

        return new { updated = changed.Count, changed, skipped = false, platform };
    }

    /// <summary>写管理员（新键）+ 让运行中的解析器立即生效。</summary>
    private async Task PersistAdminsAsync(string platform, List<string> admins, CancellationToken ct)
    {
        await _settings.SetAsync(AccessKeys.Admins(platform), string.Join(',', admins), ct).ConfigureAwait(false);

        if (_roles is ConfiguredRoleResolver resolver)
        {
            resolver.SetAdmins(platform, admins);
        }

        if (AccessKeys.HasLegacyKeys(platform))
        {
            // 旧键也写一份：万一回退到旧版程序，管理员表不会突然变空
            await _settings.SetAsync(AccessKeys.LegacyAdminsKey, string.Join(',', admins), ct).ConfigureAwait(false);
        }
    }

    /// <summary>两个集合是否等价（忽略顺序与大小写）。</summary>
    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        new HashSet<string>(a, StringComparer.OrdinalIgnoreCase)
            .SetEquals(b);

    /// <summary>条目是否是「该平台上某账号的私聊会话」（<c>qq:p123</c>）。</summary>
    private static bool IsPrivateSessionOf(string platform, string entry, string userId) =>
        string.Equals(entry.Trim(), $"{platform}:p{userId}", StringComparison.OrdinalIgnoreCase);

    private static string ParseMode(string? raw)
    {
        if (!Enum.TryParse<ChatListMode>(raw, ignoreCase: true, out var parsed))
        {
            throw new ApiException(ApiResponse.BadRequest,
                $"不支持的名单模式：{raw}（Whitelist / Blacklist）。");
        }

        return parsed.ToString();
    }

    private static List<string> ReadStringArray(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ApiException(ApiResponse.BadRequest, $"{field} 必须是字符串数组。");
        }

        return element
            .EnumerateArray()
            .Select(e => e.GetString()?.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }
}
