using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Server.Ai;
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
/// 明文 key 只在写入这一跳里短暂出现（<c>Protect</c> 前后），落库后整个系统只见密文；
/// 2026-11-23 密钥收敛后，运行期读明文的是 <see cref="IDeepSeekKeyProvider"/>（唯一的口子）。
/// </para>
/// <para>
/// 首次启动时库里没有端点 → 用环境变量里的值做**一次性种子**，之后只认库。
/// 这样既不会打破既有部署，也不会长期维护"环境变量 + 库"两份真相。
/// </para>
/// </remarks>
public sealed class RuntimeConfigService
{
    private const string ChatRole = "Chat";

    /// <summary>
    /// 端点名是**固定的几个**（问题记录 #12）：服务端以 name 为键，允许用户随便改等于允许边生成新端点。
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedEndpointNames = ["primary", "judge"];

    private readonly IConfigStore _store;
    private readonly ISecretProtector _protector;
    private readonly IChatClientProvider _clients;
    private readonly IDeepSeekKeyProvider _keys;
    private readonly ILogger _logger;

    public RuntimeConfigService(
        IConfigStore store,
        ISecretProtector protector,
        IChatClientProvider clients,
        IDeepSeekKeyProvider keys,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _protector = protector;
        _clients = clients;
        _keys = keys;
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
        var key = _keys.GetStatus();

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

            // 单 key 收敛（问题记录 06 §5）：客户端只认这一块，**明文永不回显**
            apiKey = new
            {
                configured = key.Configured,
                masked = key.Masked,
                slots = key.Slots,
            },

            // #12：端点名只读，界面直接用它建下拉
            endpointNames = AllowedEndpointNames,
        };
    }

    /// <summary>当前库里的端点名（给 setConfig 做写入前的整体校验，避免"端点已写、roles 报错"）。</summary>
    public async Task<HashSet<string>> GetEndpointNamesAsync(CancellationToken ct = default)
    {
        var endpoints = await _store.ListEndpointsAsync(ct).ConfigureAwait(false);
        return endpoints.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>写端点：校验 → 加密 → 落库 → 审计 → 热更新。</summary>
    /// <returns>是否真的发生了变更（全等时跳过写库与审计，问题记录 #10）。</returns>
    public async Task<bool> SetEndpointAsync(EndpointInput input, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ApiException(ApiResponse.BadRequest, "endpoint.name 不能为空。");
        }

        var name = input.Name.Trim();

        // #12：端点名固定，改个名字不该等于「新建一个端点」
        if (!AllowedEndpointNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new ApiException(
                ApiResponse.BadRequest,
                $"端点名是固定的：{string.Join(" / ", AllowedEndpointNames)}。不能新建或改名。");
        }

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
                    $"endpoint.apiKind 只能是 {nameof(LlmApiKind.Responses)} / {nameof(LlmApiKind.ChatCompletions)} / {nameof(LlmApiKind.Decisions)}（也接受 Chat 作为别名）。");
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

        // #10：值完全一致 → 不写库、不写审计、不触发重载
        if (existing is not null &&
            existing.BaseUrl == setting.BaseUrl &&
            existing.Model == setting.Model &&
            existing.ApiKind == setting.ApiKind &&
            !keyChanged)
        {
            return false;
        }

        // 兼容旧客户端 / 旧脚本：它们仍会往 primary 端点里塞 key。
        // 密钥已收敛，那一路写进来的 key 必须**同时**落到全局槽位，否则「改了没生效」会很难查。
        //
        // ⚠️ 顺序刻意是「先槽位、后端点」：槽位是**唯一真相**，端点密文只是留档。
        // 两次写库之间没有事务（SqliteDatabase 每次写各开连接），所以必须让失败态落在
        // 「槽位已换、端点还是旧密文」—— 那只是留档没跟上，行为正确；反过来则是
        // 「界面说已保存、实际用的还是旧 key」，属于最难查的一种故障。
        if (keyChanged && string.Equals(name, "primary", StringComparison.OrdinalIgnoreCase))
        {
            await _keys.SetKeyAsync(input.Key!, actor, ct).ConfigureAwait(false);
        }

        try
        {
            await _store.UpsertEndpointAsync(setting, ct).ConfigureAwait(false);
            await _store
                .AppendAuditAsync(keyChanged ? "setEndpoint(+key)" : "setEndpoint", actor, name, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // 放在 finally 里：槽位已经换掉了，客户端必须跟着重建 ——
            // 只在前一步成功时才 reload，就会出现「生效 key 是新的、ChatClientProvider 手上还是旧的」，
            // 那正是这条兼容路径最容易骗人的一种状态。
            await _clients.ReloadAsync(ct).ConfigureAwait(false);
        }

        _logger.Information("运行配置已更新：端点={Name}（key {KeyState}）", name, keyChanged ? "已更新" : "未变");
        return true;
    }

    /// <summary>
    /// 写入**唯一的** DeepSeek key（问题记录 06 §5 / 方案 G7）。
    /// </summary>
    /// <remarks>
    /// 只动槽位 0：其余留档槽位原样保留，这就是「对外一把、内部保留多密钥写法」。
    /// </remarks>
    /// <returns>是否真的发生了变更（同一把 key 重复保存不算）。</returns>
    public async Task<bool> SetApiKeyAsync(string key, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ApiException(ApiResponse.BadRequest, "apiKey 不能为空。");
        }

        var trimmed = key.Trim();

        // 太短的 key 一定是填错了：宁可在这里拒掉，也不要等到第一次对话 401
        if (trimmed.Length < 8)
        {
            throw new ApiException(ApiResponse.BadRequest, "apiKey 看起来不完整（少于 8 个字符）。");
        }

        var changed = await _keys.SetKeyAsync(trimmed, actor, ct).ConfigureAwait(false);
        if (!changed)
        {
            return false;
        }

        // 换 key 后要重建 LLM 客户端（以及搜索侧的快照），否则新 key 要等下次重启才生效
        await _clients.ReloadAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>改角色映射（<c>chat</c> / <c>judge</c>）→ 触发热更新。</summary>
    /// <returns>是否真的发生了变更。</returns>
    public async Task<bool> SetRoleAsync(string role, string endpointName, string actor, CancellationToken ct = default)
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

        var roles = await _store.ListRolesAsync(ct).ConfigureAwait(false);
        var current = roles.FirstOrDefault(r => string.Equals(r.Role, role, StringComparison.OrdinalIgnoreCase));
        if (current is not null &&
            string.Equals(current.EndpointName, endpointName, StringComparison.OrdinalIgnoreCase))
        {
            return false;   // #10：映射没变
        }

        await _store.SetRoleAsync(role, endpointName, ct).ConfigureAwait(false);
        await _store.AppendAuditAsync("setRole", actor, $"{role}->{endpointName}", ct).ConfigureAwait(false);
        await _clients.ReloadAsync(ct).ConfigureAwait(false);
        return true;
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
