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
/// 以前这三样只能改环境变量 + 重启；而「谁能指挥它动手」「它愿意理谁」
/// 恰恰是最需要随时调整的东西。
/// </remarks>
public sealed class GetAccessAction : IApiAction
{
    public const string AdminsKey = "access.admins";
    public const string FilterModeKey = "access.filterMode";
    public const string FilterEntriesKey = "access.filterEntries";

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
        var modeRaw = await _settings.GetAsync(FilterModeKey, ct).ConfigureAwait(false);
        var entriesRaw = await _settings.GetAsync(FilterEntriesKey, ct).ConfigureAwait(false);
        var ownerId = await _settings.GetAsync(GetOwnerAction.OwnerKey, ct).ConfigureAwait(false);

        var admins = string.IsNullOrWhiteSpace(adminsRaw)
            ? (_roles is ConfiguredRoleResolver c ? c.Admins.ToList() : [])
            : adminsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        IReadOnlyList<ContactInfo> friends;
        try
        {
            friends = await _contacts.GetFriendsAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            friends = [];
        }

        return new
        {
            ownerId,
            admins,
            filterMode = string.IsNullOrWhiteSpace(modeRaw) ? _filter.Mode.ToString() : modeRaw,
            filterEntries = string.IsNullOrWhiteSpace(entriesRaw)
                ? new List<string>()
                : entriesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            filterCount = _filter.Count,
            candidates = friends
                .Select(f => new
                {
                    userId = f.UserId,
                    nickname = string.IsNullOrWhiteSpace(f.Remark) ? f.Nickname : $"{f.Nickname}（{f.Remark}）",
                })
                .ToList(),
        };
    }
}

/// <summary>
/// 改写人员与名单（第 14 项延伸）：**写库 + 立即生效**。
/// </summary>
/// <remarks>
/// payload：<c>{ "admins": ["123"], "filterMode": "Whitelist", "filterEntries": ["qq:p123"] }</c>
/// 三者都可单独给。
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

        if (payload.TryGetProperty("admins", out var adminsElement))
        {
            if (adminsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ApiException(ApiResponse.BadRequest, "admins 必须是字符串数组。");
            }

            var admins = adminsElement
                .EnumerateArray()
                .Select(e => e.GetString()?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList();

            await _settings.SetAsync(GetAccessAction.AdminsKey, string.Join(',', admins), ct).ConfigureAwait(false);
            if (_roles is ConfiguredRoleResolver resolver)
            {
                resolver.SetAdmins(admins);   // 立即生效
            }

            changed.Add($"admins={admins.Count}");
        }

        string? mode = null;
        var entries = new List<string>();

        if (payload.TryGetProperty("filterMode", out var modeElement))
        {
            mode = modeElement.GetString()?.Trim();
            if (!Enum.TryParse<ChatListMode>(mode, ignoreCase: true, out var parsed))
            {
                throw new ApiException(ApiResponse.BadRequest, $"不支持的名单模式：{mode}（Whitelist / Blacklist）。");
            }

            mode = parsed.ToString();
        }

        if (payload.TryGetProperty("filterEntries", out var entriesElement))
        {
            if (entriesElement.ValueKind != JsonValueKind.Array)
            {
                throw new ApiException(ApiResponse.BadRequest, "filterEntries 必须是字符串数组（qq:g123 / qq:p123）。");
            }

            entries = entriesElement
                .EnumerateArray()
                .Select(e => e.GetString()?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList();
        }

        if (mode is not null || payload.TryGetProperty("filterEntries", out _))
        {
            var effectiveMode = mode ?? _filter.Mode.ToString();

            if (mode is not null)
            {
                await _settings.SetAsync(GetAccessAction.FilterModeKey, mode, ct).ConfigureAwait(false);
            }

            if (payload.TryGetProperty("filterEntries", out _))
            {
                await _settings
                    .SetAsync(GetAccessAction.FilterEntriesKey, string.Join(',', entries), ct)
                    .ConfigureAwait(false);
            }

            // 立即生效：过滤器持有可变名单
            _filter.Update(new ChatFilterOptions
            {
                Mode = Enum.Parse<ChatListMode>(effectiveMode, ignoreCase: true),
                Entries = payload.TryGetProperty("filterEntries", out _) ? entries : CurrentEntries(),
            });

            changed.Add($"filter={effectiveMode}/{_filter.Count}");
        }

        if (changed.Count == 0)
        {
            throw new ApiException(ApiResponse.BadRequest, "没有可写入的字段（admins / filterMode / filterEntries）。");
        }

        await _configs
            .AppendAuditAsync("setAccess", "http-client", string.Join(",", changed), ct)
            .ConfigureAwait(false);

        return new { updated = changed.Count, changed };
    }

    /// <summary>没改条目时保留现有条目（避免"只改模式"把名单清空）。</summary>
    private List<string> CurrentEntries() => _filter.Entries.ToList();
}
