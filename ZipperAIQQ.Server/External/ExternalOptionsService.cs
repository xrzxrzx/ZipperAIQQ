using Serilog;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.External;

/// <summary>外部 WS 的可热更新配置快照。</summary>
public sealed record ExternalSnapshot(
    bool AllowSend,
    IReadOnlyList<string> AllowedSessions,
    IReadOnlyList<ExternalReverseServer> ReverseServers,
    IReadOnlyList<ExternalForwardServer> ForwardServers);

/// <summary>
/// 外部 WS 配置（客户端可改）：优先读 <c>RuntimeSettings</c>，没有则用启动配置。
/// </summary>
/// <remarks>
/// 正向连接需要在配置变更时重连，所以这里暴露 <see cref="Changed"/> 事件；
/// token 只存不回显（客户端只看到每块的 <c>hasToken</c>）。
/// </remarks>
public sealed class ExternalOptionsService
{
    public const string AllowSendKey = "external.allowSend";
    public const string AllowedSessionsKey = "external.allowedSessions";
    public const string ReverseServersKey = "external.reverseServers";
    public const string ForwardServersKey = "external.forwardServers";

    private readonly IRuntimeSettingsStore _settings;
    private readonly ServerOptions _bootstrap;
    private readonly ILogger _logger;
    private volatile ExternalSnapshot _current;

    public ExternalOptionsService(
        IRuntimeSettingsStore settings,
        ServerOptions bootstrap,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _bootstrap = bootstrap;
        _logger = logger;

        IReadOnlyList<ExternalForwardServer> forward = string.IsNullOrWhiteSpace(bootstrap.ExternalForwardUrl)
            ? Array.Empty<ExternalForwardServer>()
            : new[] { new ExternalForwardServer(bootstrap.ExternalForwardUrl, bootstrap.ExternalForwardToken) };

        _current = new ExternalSnapshot(
            bootstrap.ExternalAllowSend,
            bootstrap.ExternalAllowedSessions,
            bootstrap.ExternalReverseServers,
            forward);
    }

    public ExternalSnapshot Current => _current;

    /// <summary>配置变化（用于正向 WS 立即重连）。</summary>
    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var allow = await _settings.GetAsync(AllowSendKey, ct).ConfigureAwait(false);
        var sessions = await _settings.GetAsync(AllowedSessionsKey, ct).ConfigureAwait(false);
        var reverseJson = await _settings.GetAsync(ReverseServersKey, ct).ConfigureAwait(false);
        var forwardJson = await _settings.GetAsync(ForwardServersKey, ct).ConfigureAwait(false);

        _current = new ExternalSnapshot(
            allow is null ? _bootstrap.ExternalAllowSend : allow is "1" or "true" or "True",
            sessions is null
                ? _bootstrap.ExternalAllowedSessions
                : sessions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Parse(reverseJson, _bootstrap.ExternalReverseServers, "external.reverseServers"),
            Parse(forwardJson, _current.ForwardServers, "external.forwardServers"));

        _logger.Information(
            "外部 WS 配置：allowSend={Allow} reverse={Reverse} forward={Forward}",
            _current.AllowSend,
            _current.ReverseServers.Count,
            _current.ForwardServers.Count);
    }

    public async Task UpdateAsync(
        bool allowSend,
        IReadOnlyList<string> allowedSessions,
        IReadOnlyList<ExternalReverseServer> reverseServers,
        IReadOnlyList<ExternalForwardServer> forwardServers,
        CancellationToken ct = default)
    {
        var sessions = string.Join(',', allowedSessions.Select(s => s.Trim()).Where(s => s.Length > 0));

        await _settings.SetAsync(AllowSendKey, allowSend ? "true" : "false", ct).ConfigureAwait(false);
        await _settings.SetAsync(AllowedSessionsKey, sessions, ct).ConfigureAwait(false);
        await _settings.SetAsync(
            ReverseServersKey,
            System.Text.Json.JsonSerializer.Serialize(reverseServers),
            ct).ConfigureAwait(false);
        await _settings.SetAsync(
            ForwardServersKey,
            System.Text.Json.JsonSerializer.Serialize(forwardServers),
            ct).ConfigureAwait(false);

        _current = new ExternalSnapshot(allowSend, allowedSessions, reverseServers, forwardServers);

        _logger.Information("外部 WS 配置已更新（allowSend={Allow}）", allowSend);
        Changed?.Invoke();
    }

    private IReadOnlyList<T> Parse<T>(string? json, IReadOnlyList<T> fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<T>>(json) ?? fallback;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "解析 {Key} 失败，沿用启动配置", key);
            return fallback;
        }
    }
}
