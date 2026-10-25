using System.Globalization;
using Serilog;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.External;

/// <summary>外部 WS 的可热更新配置快照。</summary>
public sealed record ExternalSnapshot(
    bool AllowSend,
    IReadOnlyList<string> AllowedSessions,
    string? ForwardUrl,
    bool HasForwardToken,
    IReadOnlyList<ExternalReverseServer> ReverseServers);

/// <summary>
/// 外部 WS 配置（客户端可改）：优先读 <c>RuntimeSettings</c>，没有则用启动配置。
/// </summary>
/// <remarks>
/// 正向连接需要在配置变更时重连，所以这里暴露 <see cref="Changed"/> 事件；
/// token 只存不回显（客户端只看到 <c>hasForwardToken</c>）。
/// </remarks>
public sealed class ExternalOptionsService
{
    public const string AllowSendKey = "external.allowSend";
    public const string AllowedSessionsKey = "external.allowedSessions";
    public const string ForwardUrlKey = "external.forwardUrl";
    public const string ForwardTokenKey = "external.forwardToken";
    public const string ReverseServersKey = "external.reverseServers";

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
        _current = new ExternalSnapshot(
            bootstrap.ExternalAllowSend,
            bootstrap.ExternalAllowedSessions,
            bootstrap.ExternalForwardUrl,
            !string.IsNullOrWhiteSpace(bootstrap.ExternalForwardToken),
            bootstrap.ExternalReverseServers);
    }

    public ExternalSnapshot Current => _current;

    /// <summary>配置变化（用于正向 WS 立即重连）。</summary>
    public event Action? Changed;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var allow = await _settings.GetAsync(AllowSendKey, ct).ConfigureAwait(false);
        var sessions = await _settings.GetAsync(AllowedSessionsKey, ct).ConfigureAwait(false);
        var url = await _settings.GetAsync(ForwardUrlKey, ct).ConfigureAwait(false);
        var token = await _settings.GetAsync(ForwardTokenKey, ct).ConfigureAwait(false);
        var reverseJson = await _settings.GetAsync(ReverseServersKey, ct).ConfigureAwait(false);

        var reverse = _bootstrap.ExternalReverseServers;
        if (!string.IsNullOrWhiteSpace(reverseJson))
        {
            try
            {
                reverse = System.Text.Json.JsonSerializer
                    .Deserialize<List<ExternalReverseServer>>(reverseJson) ?? [];
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "解析 external.reverseServers 失败，沿用启动配置");
            }
        }

        _current = new ExternalSnapshot(
            allow is null ? _bootstrap.ExternalAllowSend : allow is "1" or "true" or "True",
            sessions is null
                ? _bootstrap.ExternalAllowedSessions
                : sessions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            url ?? _bootstrap.ExternalForwardUrl,
            !string.IsNullOrWhiteSpace(token ?? _bootstrap.ExternalForwardToken),
            reverse);

        _logger.Information(
            "外部 WS 配置：allowSend={Allow} sessions={Sessions} forwardUrl={Url}",
            _current.AllowSend,
            string.Join(',', _current.AllowedSessions),
            _current.ForwardUrl ?? "(未配置)");
    }

    public async Task UpdateAsync(
        bool allowSend,
        IReadOnlyList<string> allowedSessions,
        string? forwardUrl,
        string? forwardToken,
        IReadOnlyList<ExternalReverseServer> reverseServers,
        CancellationToken ct = default)
    {
        var sessions = string.Join(',', allowedSessions.Select(s => s.Trim()).Where(s => s.Length > 0));

        await _settings.SetAsync(AllowSendKey, allowSend ? "true" : "false", ct).ConfigureAwait(false);
        await _settings.SetAsync(AllowedSessionsKey, sessions, ct).ConfigureAwait(false);
        await _settings.SetAsync(ForwardUrlKey, forwardUrl ?? string.Empty, ct).ConfigureAwait(false);
        await _settings.SetAsync(
            ReverseServersKey,
            System.Text.Json.JsonSerializer.Serialize(reverseServers),
            ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(forwardToken))
        {
            await _settings.SetAsync(ForwardTokenKey, forwardToken.Trim(), ct).ConfigureAwait(false);
        }

        var hasToken = !string.IsNullOrWhiteSpace(forwardToken) || _current.HasForwardToken;
        _current = new ExternalSnapshot(
            allowSend,
            allowedSessions,
            string.IsNullOrWhiteSpace(forwardUrl) ? null : forwardUrl.Trim(),
            hasToken,
            reverseServers);

        _logger.Information("外部 WS 配置已更新（allowSend={Allow}）", allowSend);
        Changed?.Invoke();
    }

    public async Task<string?> GetForwardTokenAsync(CancellationToken ct = default)
    {
        var stored = await _settings.GetAsync(ForwardTokenKey, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stored) ? _bootstrap.ExternalForwardToken : stored;
    }
}
