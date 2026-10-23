using System.Text.Json;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 人员与名单配置（用户长期要求：管理员 / 主人 / 黑白名单都要能在客户端配）。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-23 起名单模型改为**黑白两份并存 + 同条目互斥**（问题记录 #20）：
/// 旧键 <c>access.filterMode</c> / <c>access.filterEntries</c> 只用于惰性迁移与旧客户端兼容，
/// 新键是 <c>access.whitelist</c> / <c>access.blacklist</c>。
/// </para>
/// <para>
/// 主人放行范围（私聊 + 主人所在的群）不在这里写 —— 由 <c>OwnerScopeResolver</c> 计算后
/// 写入过滤器，本类只负责读出来展示。
/// </para>
/// </remarks>
public sealed class GetAccessAction : IApiAction
{
    public const string AdminsKey = "access.admins";
    public const string WhitelistKey = "access.whitelist";
    public const string BlacklistKey = "access.blacklist";

    /// <summary>旧键：只保留读取（迁移用），新写入不再使用。</summary>
    public const string LegacyModeKey = "access.filterMode";

    /// <summary>旧键：只保留读取（迁移用）。</summary>
    public const string LegacyEntriesKey = "access.filterEntries";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IRoleResolver _roles;
    private readonly IChatFilter _filter;
    private readonly IContactDirectory _contacts;

    public GetAccessAction(
        IRuntimeSettingsStore settings,
        IRoleResolver roles,
        IChatFilter filter,
        IContactDirectory contacts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(contacts);

        _settings = settings;
        _roles = roles;
        _filter = filter;
        _contacts = contacts;
    }

    public string Name => "getAccess";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var adminsRaw = await _settings.GetAsync(AdminsKey, ct).ConfigureAwait(false);
        var whitelistRaw = await _settings.GetAsync(WhitelistKey, ct).ConfigureAwait(false);
        var blacklistRaw = await _settings.GetAsync(BlacklistKey, ct).ConfigureAwait(false);
        var ownerId = await _settings.GetAsync(GetOwnerAction.OwnerKey, ct).ConfigureAwait(false);

        var admins = string.IsNullOrWhiteSpace(adminsRaw)
            ? (_roles is ConfiguredRoleResolver c ? c.Admins.ToList() : [])
            : SplitList(adminsRaw);

        var whitelist = SplitList(whitelistRaw);
        var blacklist = SplitList(blacklistRaw);

        // 惰性迁移：新键都为空时，把旧「模式 + 单名单」读成对应的一份
        if (whitelist.Count == 0 && blacklist.Count == 0)
        {
            var legacyEntries = SplitList(await _settings.GetAsync(LegacyEntriesKey, ct).ConfigureAwait(false));
            if (legacyEntries.Count > 0)
            {
                var legacyMode = await _settings.GetAsync(LegacyModeKey, ct).ConfigureAwait(false);
                if (string.Equals(legacyMode, nameof(ChatListMode.Whitelist), StringComparison.OrdinalIgnoreCase))
                {
                    whitelist = legacyEntries;
                }
                else
                {
                    blacklist = legacyEntries;
                }
            }
        }

        IReadOnlyList<ContactInfo> friends;
        try
        {
            friends = await _contacts.GetFriendsAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            friends = [];
        }

        // 名单条目的候选需要群会话（#26）：好友填不了 qq:g…
        IReadOnlyList<GroupInfo> groups;
        try
        {
            groups = await _contacts.GetGroupsAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            groups = [];
        }

        return new
        {
            ownerId,
            admins,

            // 新模型：两份名单 + 主人放行范围
            whitelist,
            blacklist,
            alwaysAllow = _filter.AlwaysAllow,

            // 旧字段（B4 之前的老客户端还在用）：按「白名单优先」折算成单份
            filterMode = whitelist.Count > 0 ? nameof(ChatListMode.Whitelist) : nameof(ChatListMode.Blacklist),
            filterEntries = whitelist.Count > 0 ? whitelist : blacklist,
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
                    sessionId = $"qq:g{g.GroupId}",
                })
                .ToList(),
        };
    }

    internal static List<string> SplitList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>
/// 改写人员与名单（第 14 项延伸）：**写库 + 立即生效**。
/// </summary>
/// <remarks>
/// <para>
/// 新 payload：<c>{ "admins": ["123"], "whitelist": ["qq:p123"], "blacklist": ["qq:g456"] }</c>，
/// 三者都可单独给。
/// </para>
/// <para>
/// 旧 payload（<c>filterMode</c> + <c>filterEntries</c>）继续接受：折算成单份名单，另一份清空 ——
/// 这样老客户端在 B4 上线前仍然可用。
/// </para>
/// <para>
/// <b>管理员 × 黑名单冲突</b>（问题记录 #23）：首次提交若发现管理员在黑名单里，返回 <c>409</c>
/// 并列出冲突账号；带 <c>confirmDemoteAdmins: true</c> 重发则**剥夺管理员并拉黑**。
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

        var legacyMode = payload.TryGetProperty("filterMode", out var modeElement)
            ? ParseMode(modeElement.GetString())
            : null;
        var legacyEntries = payload.TryGetProperty("filterEntries", out var entriesElement)
            ? ReadStringArray(entriesElement, "filterEntries")
            : null;

        if (legacyMode is not null || legacyEntries is not null)
        {
            if (whitelistInput is not null || blacklistInput is not null)
            {
                throw new ApiException(ApiResponse.BadRequest,
                    "filterMode / filterEntries 是旧字段，不能与 whitelist / blacklist 同时提交。");
            }

            var mode = legacyMode
                ?? (_filter.WhitelistCount > 0 ? nameof(ChatListMode.Whitelist) : nameof(ChatListMode.Blacklist));
            var entries = legacyEntries
                ?? (mode == nameof(ChatListMode.Whitelist)
                    ? _filter.Whitelist.ToList()
                    : _filter.Blacklist.ToList());

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

        var nextWhitelist = whitelistInput ?? _filter.Whitelist.ToList();
        var nextBlacklist = blacklistInput ?? _filter.Blacklist.ToList();

        var overlap = nextWhitelist
            .Intersect(nextBlacklist, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (overlap.Count > 0)
        {
            throw new ApiException(ApiResponse.BadRequest,
                $"同一条目不能同时在白名单与黑名单：{string.Join("、", overlap)}");
        }

        // ── ③ 管理员 × 黑名单冲突（#23）──
        if (admins is not null)
        {
            var conflicts = admins
                .Where(a => nextBlacklist.Any(e => IsPrivateSessionOf(e, a)))
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
                        new { code = "admin_in_blacklist", admins = conflicts });
                }

                admins = admins.Except(conflicts, StringComparer.OrdinalIgnoreCase).ToList();
                await _settings.SetAsync(GetAccessAction.AdminsKey, string.Join(',', admins), ct)
                    .ConfigureAwait(false);

                if (_roles is ConfiguredRoleResolver resolver)
                {
                    resolver.SetAdmins(admins);
                }

                await _configs
                    .AppendAuditAsync("demoteAdmin", "http-client", string.Join(",", conflicts), ct)
                    .ConfigureAwait(false);

                changed.Add($"demoteAdmins={conflicts.Count}");
            }
            else
            {
                // #10：与库里当前管理员一致 → 不写、不审计（但不要提前返回：名单可能还在后面要写）
                var storedAdmins = GetAccessAction.SplitList(
                    await _settings.GetAsync(GetAccessAction.AdminsKey, ct).ConfigureAwait(false));

                var currentAdmins = storedAdmins.Count > 0
                    ? storedAdmins
                    : ((_roles as ConfiguredRoleResolver)?.Admins ?? []).ToList();

                if (!SameSet(admins, currentAdmins))
                {
                    await _settings.SetAsync(GetAccessAction.AdminsKey, string.Join(',', admins), ct)
                        .ConfigureAwait(false);

                    if (_roles is ConfiguredRoleResolver resolver)
                    {
                        resolver.SetAdmins(admins);
                    }

                    changed.Add($"admins={admins.Count}");
                }
            }
        }

        // ── ④ 写名单 + 立即生效 ──
        if (whitelistInput is not null || blacklistInput is not null)
        {
            // #10：运行中的名单与提交值一致 → 跳过（列表没变就不该每次写审计）
            var listsUnchanged =
                SameSet(nextWhitelist, _filter.Whitelist) &&
                SameSet(nextBlacklist, _filter.Blacklist);

            if (listsUnchanged)
            {
                // 名单没变：跳过写库与审计，但仍要回落「有没有别的字段变更」
                if (changed.Count == 0)
                {
                    return new { updated = 0, skipped = true, changed };
                }
            }
            else
            {
                await _settings.SetAsync(GetAccessAction.WhitelistKey, string.Join(',', nextWhitelist), ct)
                    .ConfigureAwait(false);
                await _settings.SetAsync(GetAccessAction.BlacklistKey, string.Join(',', nextBlacklist), ct)
                    .ConfigureAwait(false);

                // 清掉旧键：否则下次读取会拿旧值做「惰性迁移」，看起来像名单自己变回来了
                await _settings.SetAsync(GetAccessAction.LegacyModeKey, string.Empty, ct).ConfigureAwait(false);
                await _settings.SetAsync(GetAccessAction.LegacyEntriesKey, string.Empty, ct).ConfigureAwait(false);

                _filter.Update(new ChatFilterOptions
                {
                    Whitelist = nextWhitelist,
                    Blacklist = nextBlacklist,
                    AlwaysAllow = _filter.AlwaysAllow.ToList(),
                });

                changed.Add($"whitelist={nextWhitelist.Count}/blacklist={nextBlacklist.Count}");
            }
        }

        if (changed.Count == 0)
        {
            throw new ApiException(ApiResponse.BadRequest,
                "没有可写入的字段（admins / whitelist / blacklist / filterMode / filterEntries）。");
        }

        await _configs
            .AppendAuditAsync("setAccess", "http-client", string.Join(",", changed), ct)
            .ConfigureAwait(false);

        return new { updated = changed.Count, changed, skipped = false };
    }

    /// <summary>两个集合是否等价（忽略顺序与大小写）。</summary>
    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        new HashSet<string>(a, StringComparer.OrdinalIgnoreCase)
            .SetEquals(b);

    /// <summary>条目是否是某账号的私聊会话（<c>qq:p123</c>）。</summary>
    private static bool IsPrivateSessionOf(string entry, string userId) =>
        entry.EndsWith(":p" + userId, StringComparison.OrdinalIgnoreCase);

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
