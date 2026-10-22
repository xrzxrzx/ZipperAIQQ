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

        var didSomething = false;

        if (payload.TryGetProperty("endpoint", out var endpoint) && endpoint.ValueKind == JsonValueKind.Object)
        {
            var input = new EndpointInput(
                Name: PayloadReader.GetString(endpoint, "name"),
                BaseUrl: PayloadReader.GetString(endpoint, "baseUrl"),
                Model: PayloadReader.GetString(endpoint, "model"),
                ApiKind: PayloadReader.GetString(endpoint, "apiKind"),
                Key: PayloadReader.GetString(endpoint, "key"));

            await _config.SetEndpointAsync(input, Actor, ct).ConfigureAwait(false);
            didSomething = true;
        }

        if (payload.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Object)
        {
            foreach (var role in roles.EnumerateObject())
            {
                if (role.Value.ValueKind != JsonValueKind.String)
                {
                    throw new ApiException(ApiResponse.BadRequest, $"roles.{role.Name} 必须是字符串。");
                }

                await _config.SetRoleAsync(role.Name, role.Value.GetString()!, Actor, ct).ConfigureAwait(false);
                didSomething = true;
            }
        }

        if (!didSomething)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 里至少要给 endpoint 或 roles 之一。");
        }

        // 回最新视图（掩码），客户端不用再调一次 getConfig
        return await _config.GetViewAsync(ct).ConfigureAwait(false);
    }
}
