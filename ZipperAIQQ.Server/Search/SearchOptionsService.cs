using Serilog;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Search;

/// <summary>网页搜索配置（与模型端点完全独立：自己的 baseUrl + key）。</summary>
public sealed record SearchSnapshot(bool Enabled, string BaseUrl, bool HasKey);

/// <summary>
/// 网页搜索配置：enabled 存 RuntimeSettings，baseUrl + 加密 key 存 <c>ModelEndpoints.search</c>。
/// </summary>
/// <remarks>
/// 复用 <see cref="IConfigStore"/> 的端点表与密钥环，但**不使用** primary 的 key ——
/// 搜索服务可以换供应商 / 换 key，不影响对话模型。
/// </remarks>
public sealed class SearchOptionsService
{
    public const string EnabledKey = "search.enabled";
    public const string EndpointName = "search";
    public const string DefaultBaseUrl = "https://api.deepseek.com";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IConfigStore _configs;
    private readonly ISecretProtector _protector;
    private readonly ServerOptions _bootstrap;
    private readonly ILogger _logger;
    private volatile SearchSnapshot _current;

    public SearchOptionsService(
        IRuntimeSettingsStore settings,
        IConfigStore configs,
        ISecretProtector protector,
        ServerOptions bootstrap,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _configs = configs;
        _protector = protector;
        _bootstrap = bootstrap;
        _logger = logger;

        _current = new SearchSnapshot(
            bootstrap.SearchEnabled,
            string.IsNullOrWhiteSpace(bootstrap.SearchBaseUrl) ? DefaultBaseUrl : bootstrap.SearchBaseUrl!,
            HasKey: false);
    }

    public SearchSnapshot Current => _current;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var enabled = await _settings.GetAsync(EnabledKey, ct).ConfigureAwait(false);
        var endpoint = await _configs.GetEndpointAsync(EndpointName, ct).ConfigureAwait(false);

        var baseUrl = endpoint?.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = string.IsNullOrWhiteSpace(_bootstrap.SearchBaseUrl)
                ? DefaultBaseUrl
                : _bootstrap.SearchBaseUrl!;
        }

        _current = new SearchSnapshot(
            enabled is null ? _bootstrap.SearchEnabled : SettingsValue.IsTrue(enabled),
            baseUrl.TrimEnd('/'),
            endpoint?.KeyCipher is not null);

        _logger.Information(
            "网页搜索配置：enabled={Enabled} baseUrl={BaseUrl} hasKey={HasKey}",
            _current.Enabled, _current.BaseUrl, _current.HasKey);
    }

    public async Task UpdateAsync(
        bool enabled, string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultBaseUrl
            : baseUrl.Trim().TrimEnd('/');

        await _settings.SetAsync(EnabledKey, enabled ? "true" : "false", ct).ConfigureAwait(false);

        // apiKey 为空 = 保留原密文（SqliteConfigStore 的 COALESCE 语义）
        var cipher = string.IsNullOrWhiteSpace(apiKey) ? null : _protector.Protect(apiKey.Trim());

        await _configs.UpsertEndpointAsync(new ModelEndpointSetting(
            EndpointName,
            normalized,
            "search",
            ApiKind: 0,
            cipher,
            DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        _current = new SearchSnapshot(enabled, normalized, _current.HasKey || cipher is not null);
        _logger.Information("网页搜索配置已更新：enabled={Enabled} baseUrl={BaseUrl}", enabled, normalized);
    }

    public async Task<string?> GetKeyAsync(CancellationToken ct = default)
    {
        var endpoint = await _configs.GetEndpointAsync(EndpointName, ct).ConfigureAwait(false);
        return endpoint?.KeyCipher is null ? null : _protector.Unprotect(endpoint.KeyCipher);
    }
}
