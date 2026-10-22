using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Server.Config;

/// <summary>一次端点写入（key 可选：不给就是"只改别的不动 key"）。</summary>
public sealed record EndpointInput(string? Name, string? BaseUrl, string? Model, string? ApiKind, string? Key);

/// <summary>
/// 运行配置的读写（M2-B / FR-8）。
/// </summary>
/// <remarks>
/// <para>
/// 职责：**校验 → 加密 → 落库 → 审计 → 热更新**。
/// 明文 key 只在这个类里出现一次（<c>Protect</c> 前后），之后整个系统只见密文。
/// </para>
/// <para>
/// 首次启动时库里没有端点 → 用环境变量里的值做**一次性种子**，之后只认库。
/// 这样既不会打破既有部署，也不会长期维护"环境变量 + 库"两份真相。
/// </para>
/// </remarks>
public sealed class RuntimeConfigService
{
    private const string ChatRole = "Chat";

    private readonly IConfigStore _store;
    private readonly ISecretProtector _protector;
    private readonly IChatClientProvider _clients;
    private readonly ILogger _logger;

    public RuntimeConfigService(
        IConfigStore store,
        ISecretProtector protector,
        IChatClientProvider clients,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _protector = protector;
        _clients = clients;
        _logger = logger;
    }

    /// <summary>
    /// 首次启动：把环境变量里的 LLM 配置写成库里的第一个端点（之后只认库）。
    /// </summary>
    public async Task SeedFromEnvironmentAsync(ServerOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var existing = await _store.ListEndpointsAsync(ct).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.LlmApiKey) ||
            string.IsNullOrWhiteSpace(options.LlmBaseUrl) ||
            string.IsNullOrWhiteSpace(options.LlmModel))
        {
            _logger.Warning("运行配置为空，且环境变量里没有完整 LLM 配置 —— 对话功能不可用，请通过客户端配置");
            return;
        }

        var endpoint = new ModelEndpointSetting(
            Name: "primary",
            BaseUrl: options.LlmBaseUrl,
            Model: options.LlmModel,
            ApiKind: (int)options.LlmApiKind,
            KeyCipher: _protector.Protect(options.LlmApiKey),
            UpdatedAt: DateTimeOffset.UtcNow);

        await _store.UpsertEndpointAsync(endpoint, ct).ConfigureAwait(false);
        await _store.SetRoleAsync(ChatRole, endpoint.Name, ct).ConfigureAwait(false);
        await _store.AppendAuditAsync("seedFromEnvironment", "startup", endpoint.Name, ct).ConfigureAwait(false);

        _logger.Information(
            "已把环境变量里的 LLM 配置写入运行配置（端点={Name}，模型={Model}）。之后以库为准。",
            endpoint.Name, endpoint.Model);
    }

    /// <summary>配置视图：**key 只回掩码**（FR-8.6）。</summary>
    public async Task<object?> GetViewAsync(CancellationToken ct = default)
    {
        var endpoints = await _store.ListEndpointsAsync(ct).ConfigureAwait(false);
        var roles = await _store.ListRolesAsync(ct).ConfigureAwait(false);

        return new
        {
            endpoints = endpoints
                .Select(e => new
                {
                    name = e.Name,
                    baseUrl = e.BaseUrl,
                    model = e.Model,
                    apiKind = (LlmApiKind)e.ApiKind,
                    keyMasked = Mask(_protector.Unprotect(e.KeyCipher)),
                    updatedAt = e.UpdatedAt.ToUnixTimeMilliseconds(),
                })
                .ToList(),
            roles = roles.ToDictionary(r => r.Role, r => r.EndpointName, StringComparer.OrdinalIgnoreCase),
            hasClient = _clients.HasClient,
        };
    }

    /// <summary>写端点：校验 → 加密 → 落库 → 审计 → 热更新。</summary>
    public async Task SetEndpointAsync(EndpointInput input, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ApiException(ApiResponse.BadRequest, "endpoint.name 不能为空。");
        }

        var name = input.Name.Trim();

        // 更新已有端点时，没给的字段沿用库里的值
        var existing = await _store.GetEndpointAsync(name, ct).ConfigureAwait(false);

        var baseUrl = string.IsNullOrWhiteSpace(input.BaseUrl) ? existing?.BaseUrl : input.BaseUrl.Trim();
        var model = string.IsNullOrWhiteSpace(input.Model) ? existing?.Model : input.Model.Trim();

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ApiException(ApiResponse.BadRequest, "endpoint.baseUrl 不能为空。");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            throw new ApiException(ApiResponse.BadRequest, $"endpoint.baseUrl 不是合法 URL：{baseUrl}");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ApiException(ApiResponse.BadRequest, "endpoint.model 不能为空。");
        }

        var apiKind = existing?.ApiKind ?? (int)LlmApiKind.ChatCompletions;
        if (!string.IsNullOrWhiteSpace(input.ApiKind))
        {
            // 宽容解析：客户端/文档里都习惯写 "Chat"，而枚举叫 ChatCompletions
            var raw = input.ApiKind.Trim();
            if (raw.Equals("Chat", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("ChatCompletion", StringComparison.OrdinalIgnoreCase))
            {
                raw = nameof(LlmApiKind.ChatCompletions);
            }

            if (!Enum.TryParse<LlmApiKind>(raw, ignoreCase: true, out var parsed))
            {
                throw new ApiException(ApiResponse.BadRequest,
                    $"endpoint.apiKind 只能是 {nameof(LlmApiKind.Responses)} 或 {nameof(LlmApiKind.ChatCompletions)}（也接受 Chat 作为别名）。");
            }

            apiKind = (int)parsed;
        }

        // key：给了就加密覆盖；没给（null）保留旧密文
        string? cipher = existing?.KeyCipher;
        var keyChanged = !string.IsNullOrWhiteSpace(input.Key);
        if (keyChanged)
        {
            cipher = _protector.Protect(input.Key!.Trim());
        }

        var setting = new ModelEndpointSetting(
            Name: name,
            BaseUrl: baseUrl!,
            Model: model!,
            ApiKind: apiKind,
            KeyCipher: cipher,
            UpdatedAt: DateTimeOffset.UtcNow);

        await _store.UpsertEndpointAsync(setting, ct).ConfigureAwait(false);
        await _store
            .AppendAuditAsync(keyChanged ? "setEndpoint(+key)" : "setEndpoint", actor, name, ct)
            .ConfigureAwait(false);

        await _clients.ReloadAsync(ct).ConfigureAwait(false);

        _logger.Information("运行配置已更新：端点={Name}（key {KeyState}）", name, keyChanged ? "已更新" : "未变");
    }

    /// <summary>改角色映射（<c>chat</c> / <c>judge</c>）→ 触发热更新。</summary>
    public async Task SetRoleAsync(string role, string endpointName, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(endpointName))
        {
            throw new ApiException(ApiResponse.BadRequest, "roles 里的角色名与端点名都不能为空。");
        }

        var endpoint = await _store.GetEndpointAsync(endpointName, ct).ConfigureAwait(false);
        if (endpoint is null)
        {
            throw new ApiException(ApiResponse.BadRequest, $"端点不存在：{endpointName}");
        }

        await _store.SetRoleAsync(role, endpointName, ct).ConfigureAwait(false);
        await _store.AppendAuditAsync("setRole", actor, $"{role}->{endpointName}", ct).ConfigureAwait(false);
        await _clients.ReloadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>掩码：<c>sk-****a1b2</c>。key 少于 8 位时整体打码。</summary>
    private static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        return key.Length <= 8 ? "****" : $"{key[..3]}****{key[^4..]}";
    }
}
