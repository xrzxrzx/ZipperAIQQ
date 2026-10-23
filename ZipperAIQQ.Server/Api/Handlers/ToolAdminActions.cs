using System.Text.Json;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>列出工具 / 命令 / NapCat API 三类能力的开关与权限（问题记录 #28）。</summary>
public sealed class GetToolCatalogAction : IApiAction
{
    private readonly IExecutionPolicy _policy;

    public GetToolCatalogAction(IExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    public string Name => "getToolCatalog";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object?>(_policy.Catalog
            .Select(item => new
            {
                category = ExecutionPolicy.CategoryKey(item.Category),
                categoryLabel = CategoryLabel(item.Category),
                key = item.Key,
                label = item.Label,
                enabled = _policy.IsEnabled(item.Category, item.Key, item.DefaultEnabled),
                role = _policy.MinRole(item.Category, item.Key, item.DefaultRole).ToString(),
                defaultRole = item.DefaultRole.ToString(),
                hardFloor = item.HardFloor?.ToString(),
            })
            .ToList());

    private static string CategoryLabel(PolicyCategory category) => category switch
    {
        PolicyCategory.Tool => "工具",
        PolicyCategory.Command => "命令",
        _ => "NapCat API",
    };
}

/// <summary>
/// 写入策略（开关 + 最低角色），**即时生效**。
/// </summary>
/// <remarks>
/// payload：<c>{ "items": [ { "category":"tool", "key":"file_delete", "enabled":true, "role":"Admin" } ] }</c>
/// 低于硬底线的写入直接拒绝 —— 可配置不等于可以配成危险。
/// </remarks>
public sealed class SetToolPolicyAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IExecutionPolicy _policy;
    private readonly IConfigStore _configs;

    public SetToolPolicyAction(
        IRuntimeSettingsStore settings,
        IExecutionPolicy policy,
        IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(configs);

        _settings = settings;
        _policy = policy;
        _configs = configs;
    }

    public string Name => "setToolPolicy";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 { items: [ { category, key, enabled?, role? } ] }。");
        }

        var pending = new List<(PolicyItem Item, bool? Enabled, Role? Role)>();

        foreach (var element in items.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("category", out var categoryElement) ||
                !element.TryGetProperty("key", out var keyElement))
            {
                throw new ApiException(ApiResponse.BadRequest, "每一项都需要 category 与 key。");
            }

            var category = ParseCategory(categoryElement.GetString());
            var key = keyElement.GetString()?.Trim();
            var item = _policy.Catalog.FirstOrDefault(i =>
                i.Category == category && string.Equals(i.Key, key, StringComparison.Ordinal));

            if (item is null)
            {
                throw new ApiException(ApiResponse.BadRequest, $"目录里没有这一项：{category}/{key}");
            }

            bool? enabled = null;
            if (element.TryGetProperty("enabled", out var enabledElement))
            {
                if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ApiException(ApiResponse.BadRequest, $"{key}.enabled 必须是 true / false。");
                }

                enabled = enabledElement.GetBoolean();
            }

            Role? role = null;
            if (element.TryGetProperty("role", out var roleElement))
            {
                if (!Enum.TryParse<Role>(roleElement.GetString(), ignoreCase: true, out var parsed))
                {
                    throw new ApiException(ApiResponse.BadRequest,
                        $"{key}.role 只能是 Normal / Whitelist / Admin / Owner。");
                }

                if (item.HardFloor is { } floor && parsed < floor)
                {
                    throw new ApiException(ApiResponse.BadRequest,
                        $"{key} 的硬底线是 {floor}，不允许放宽到 {parsed}。");
                }

                role = parsed;
            }

            if (enabled is null && role is null)
            {
                throw new ApiException(ApiResponse.BadRequest, $"{key} 至少要给 enabled 或 role。");
            }

            pending.Add((item, enabled, role));
        }

        foreach (var (item, enabled, role) in pending)
        {
            if (enabled is not null)
            {
                await _settings.SetAsync(
                    ExecutionPolicy.BuildKey(item.Category, item.Key, "enabled"),
                    enabled.Value ? "1" : "0",
                    ct).ConfigureAwait(false);
            }

            if (role is not null)
            {
                await _settings.SetAsync(
                    ExecutionPolicy.BuildKey(item.Category, item.Key, "role"),
                    role.Value.ToString(),
                    ct).ConfigureAwait(false);
            }
        }

        await _policy.ReloadAsync(ct).ConfigureAwait(false);

        await _configs
            .AppendAuditAsync("setToolPolicy", Actor, string.Join(",", pending.Select(p => p.Item.Key)), ct)
            .ConfigureAwait(false);

        return new { updated = pending.Count };
    }

    private static PolicyCategory ParseCategory(string? raw) => raw?.ToLowerInvariant() switch
    {
        "tool" => PolicyCategory.Tool,
        "command" => PolicyCategory.Command,
        "channelapi" or "channel_api" or "napcat" => PolicyCategory.ChannelApi,
        _ => throw new ApiException(ApiResponse.BadRequest,
            $"category 只能是 tool / command / channelapi（收到 {raw}）。"),
    };
}
