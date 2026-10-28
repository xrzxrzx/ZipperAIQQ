using System.Text.Json;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Dto;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 写入运行配置（FR-8.5）：端点 + 可选角色映射；**保存即热更新**（FR-8.4）。
/// </summary>
/// <remarks>
/// payload 形状：
/// <code>
/// { "endpoint": { "name": "primary", "baseUrl": "...", "model": "...", "apiKind": "ChatCompletions", "key": "..." },
///   "roles":    { "chat": "primary" } }
/// </code>
/// 两个都可选，但**不能都不给**。
/// </remarks>
public sealed class SetConfigAction : IApiAction
{
    /// <summary>没有用户体系，审计主体固定成"经 HTTP 配置的客户端"。</summary>
    private const string Actor = "http-client";

    private readonly RuntimeConfigService _config;

    public SetConfigAction(RuntimeConfigService config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public string Name => "setConfig";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 必须是对象。");
        }

        var updated = 0;

        EndpointInput? pendingEndpoint = null;
        if (payload.TryGetProperty("endpoint", out var endpoint))
        {
            if (endpoint.ValueKind != JsonValueKind.Object)
            {
                throw new ApiException(ApiResponse.BadRequest, "endpoint 必须是对象。");
            }

            pendingEndpoint = new EndpointInput(
                Name: PayloadReader.GetString(endpoint, "name"),
                BaseUrl: PayloadReader.GetString(endpoint, "baseUrl"),
                Model: PayloadReader.GetString(endpoint, "model"),
                ApiKind: PayloadReader.GetString(endpoint, "apiKind"),
                Key: PayloadReader.GetString(endpoint, "key"));
        }

        var pendingRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payload.TryGetProperty("roles", out var roles))
        {
            if (roles.ValueKind != JsonValueKind.Object)
            {
                throw new ApiException(ApiResponse.BadRequest, "roles 必须是 { 角色: 端点名 } 对象。");
            }

            foreach (var role in roles.EnumerateObject())
            {
                if (role.Value.ValueKind != JsonValueKind.String)
                {
                    throw new ApiException(ApiResponse.BadRequest, $"roles.{role.Name} 必须是字符串。");
                }

                var target = role.Value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(target))
                {
                    throw new ApiException(ApiResponse.BadRequest, $"roles.{role.Name} 不能为空。");
                }

                pendingRoles[role.Name] = target;
            }
        }

        if (pendingEndpoint is null && pendingRoles.Count == 0)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 里至少要给 endpoint 或 roles 之一。");
        }

        // roles 先整体预校验：目标端点必须已存在，或就是本次要写的端点。
        // 否则会出现「端点已落库、roles 报错」的半套状态。
        if (pendingRoles.Count > 0)
        {
            var known = await _config.GetEndpointNamesAsync(ct).ConfigureAwait(false);
            if (pendingEndpoint?.Name is { Length: > 0 } name)
            {
                known.Add(name.Trim());
            }

            foreach (var (role, target) in pendingRoles)
            {
                if (!known.Contains(target))
                {
                    throw new ApiException(ApiResponse.BadRequest, $"roles.{role} 指向不存在的端点：{target}");
                }
            }
        }

        if (pendingEndpoint is not null &&
            await _config.SetEndpointAsync(pendingEndpoint, Actor, ct).ConfigureAwait(false))
        {
            updated++;
        }

        foreach (var (role, target) in pendingRoles)
        {
            if (await _config.SetRoleAsync(role, target, Actor, ct).ConfigureAwait(false))
            {
                updated++;
            }
        }

        // #10：没变化时告诉客户端「跳过」，别把它显示成"已保存"
        return new { updated, skipped = updated == 0 };
    }
}
