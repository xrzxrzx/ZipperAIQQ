using Serilog;
using ZipperAIQQ.Server.Ai;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Search;

/// <summary>网页搜索配置（与模型端点完全独立：自己的 baseUrl + key）。</summary>
public sealed record SearchSnapshot(bool Enabled, string BaseUrl, bool HasKey);

/// <summary>
/// 网页搜索配置：enabled 存 RuntimeSettings，baseUrl 存 <c>ModelEndpoints.search</c>。
/// </summary>
/// <remarks>
/// <para>
/// 复用 <see cref="IConfigStore"/> 的端点表，但**不使用** primary 的 key ——
/// 搜索服务可以换供应商 / 换地址，不影响对话模型。
/// </para>
/// <para>
/// <b>key 已收敛（问题记录 06 §5）</b>：地址仍是搜索自己的，key 走全局
/// <see cref="IDeepSeekKeyProvider"/>。端点表里 <c>search</c> 行的旧密文作为**留档回落**：
/// 全局 key 还没配时，老部署靠它照常能搜（升级后不需要立刻重填）。
/// </para>
/// </remarks>
public sealed class SearchOptionsService
{
    public const string EnabledKey = "search.enabled";
    public const string EndpointName = "search";
    public const string DefaultBaseUrl = "https://api.deepseek.com";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IConfigStore _configs;
    private readonly ISecretProtector _protector;
    private readonly IDeepSeekKeyProvider _keys;
    private readonly ServerOptions _bootstrap;
    private readonly ILogger _logger;
    private volatile SearchSnapshot _current;

    public SearchOptionsService(
        IRuntimeSettingsStore settings,
        IConfigStore configs,
        ISecretProtector protector,
        IDeepSeekKeyProvider keys,
        ServerOptions bootstrap,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _configs = configs;
        _protector = protector;
        _keys = keys;
        _bootstrap = bootstrap;
        _logger = logger;

        _current = new SearchSnapshot(
            bootstrap.SearchEnabled,
            string.IsNullOrWhiteSpace(bootstrap.SearchBaseUrl) ? DefaultBaseUrl : bootstrap.SearchBaseUrl!,
            HasKey: false);
    }

    /// <summary>
    /// 当前快照。**<c>HasKey</c> 在读的时候实时算一次**：全局 key 是别处（设置页）写的，
    /// 这个服务不知道也不该知道谁改了它；只留 Initialize 时的快照会出现「刚填完 key，搜索卡还显示未配置」。
    /// </summary>
    public SearchSnapshot Current => _current with { HasKey = _current.HasKey || _keys.HasKey };

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
            HasKey: _keys.HasKey || endpoint?.KeyCipher is not null);

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

        // apiKey 为空 = **保留**端点表里的旧密文（SqliteConfigStore 的 COALESCE 语义，
        // 这里永远传 null，所以 legacy 密文只能靠改库清掉 —— 这是有意的：它是人工恢复的留档）。
        // 给了 key 时**写全局槽位**（问题记录 06 §5：一把 key 走全链路），端点表只留地址。
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await _keys.SetKeyAsync(apiKey.Trim(), "http-client", ct).ConfigureAwait(false);
        }

        await _configs.UpsertEndpointAsync(new ModelEndpointSetting(
            EndpointName,
            normalized,
            "search",
            ApiKind: 0,
            KeyCipher: null,
            DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        _current = new SearchSnapshot(
            enabled,
            normalized,
            HasKey: _keys.HasKey || _current.HasKey);
        _logger.Information("网页搜索配置已更新：enabled={Enabled} baseUrl={BaseUrl}", enabled, normalized);
    }

    /// <summary>
    /// 取搜索用的 key：**全局 key 优先**，没有时回落到 <c>search</c> 端点的留档密文
    /// （老部署升级后不重填也能继续搜）。顺序的唯一实现见 <see cref="KeyFallback"/>。
    /// </summary>
    public async Task<string?> GetKeyAsync(CancellationToken ct = default)
    {
        return await KeyFallback
            .ResolveAsync(
                _keys,
                _protector,
                async token =>
                {
                    var endpoint = await _configs.GetEndpointAsync(EndpointName, token).ConfigureAwait(false);
                    return endpoint?.KeyCipher;
                },
                ct)
            .ConfigureAwait(false);
    }
}
