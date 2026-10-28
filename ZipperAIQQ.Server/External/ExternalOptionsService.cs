using System.Text.Json;
using Serilog;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.External;

/// <summary>外部 WS 的可热更新配置快照（token 为**明文**，仅存在于内存）。</summary>
public sealed record ExternalSnapshot(
    bool AllowSend,
    IReadOnlyList<string> AllowedSessions,
    IReadOnlyList<ExternalReverseServer> ReverseServers,
    IReadOnlyList<ExternalForwardServer> ForwardServers);

/// <summary>
/// 外部 WS 配置（客户端可改）：优先读 <c>RuntimeSettings</c>，没有则用启动配置。
/// </summary>
/// <remarks>
/// <para>
/// 正向连接需要在配置变更时重连，所以这里暴露 <see cref="Changed"/> 事件。
/// </para>
/// <para>
/// <b>token 的安全口径</b>：落库前用 <see cref="ISecretProtector"/> 加密（与 LLM / 抖音 / 邮件一致），
/// 运行时快照里是明文；<c>getExternal</c> **不回显明文**，只回 <c>tokenConfigured</c>。
/// 旧版本存的明文 token 在启动时**懒迁移**成密文。
/// </para>
/// </remarks>
public sealed class ExternalOptionsService
{
    public const string AllowSendKey = "external.allowSend";
    public const string AllowedSessionsKey = "external.allowedSessions";
    public const string ReverseServersKey = "external.reverseServers";
    public const string ForwardServersKey = "external.forwardServers";

    private readonly IRuntimeSettingsStore _settings;
    private readonly ServerOptions _bootstrap;
    private readonly ISecretProtector _protector;
    private readonly ILogger _logger;
    private volatile ExternalSnapshot _current;

    public ExternalOptionsService(
        IRuntimeSettingsStore settings,
        ServerOptions bootstrap,
        ISecretProtector protector,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _bootstrap = bootstrap;
        _protector = protector;
        _logger = logger;

        IReadOnlyList<ExternalForwardServer> forward = string.IsNullOrWhiteSpace(bootstrap.ExternalForwardUrl)
            ? Array.Empty<ExternalForwardServer>()
            : new[]
            {
                new ExternalForwardServer
                {
                    Name = "启动配置",
                    Url = bootstrap.ExternalForwardUrl,
                    Token = bootstrap.ExternalForwardToken,
                },
            };

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

        var (reverse, migratedFromReverse) = ParseReverse(reverseJson, _bootstrap.ExternalReverseServers);
        var (forward, migratedFromForward) = ParseForward(forwardJson, _current.ForwardServers);

        _current = new ExternalSnapshot(
            allow is null ? _bootstrap.ExternalAllowSend : SettingsValue.IsTrue(allow),
            sessions is null
                ? _bootstrap.ExternalAllowedSessions
                : sessions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            reverse,
            forward);

        // 旧版本把 token 明文写进 JSON：第一次启动时改成密文（只写一次）
        if (migratedFromReverse || migratedFromForward)
        {
            await PersistAsync(_current, ct).ConfigureAwait(false);
            _logger.Information("外部 WS 的旧明文 token 已迁移为密文");
        }

        _logger.Information(
            "外部 WS 配置：allowSend={Allow} reverse={Reverse} forward={Forward}",
            _current.AllowSend,
            _current.ReverseServers.Count,
            _current.ForwardServers.Count);
    }

    /// <summary>保存配置（token 一律加密落库；内存快照保持明文）。</summary>
    public async Task UpdateAsync(
        bool allowSend,
        IReadOnlyList<string> allowedSessions,
        IReadOnlyList<ExternalReverseServer> reverseServers,
        IReadOnlyList<ExternalForwardServer> forwardServers,
        CancellationToken ct = default)
    {
        var next = new ExternalSnapshot(allowSend, allowedSessions, reverseServers, forwardServers);

        await PersistAsync(next, ct).ConfigureAwait(false);

        _current = next;

        _logger.Information("外部 WS 配置已更新（allowSend={Allow}）", allowSend);
        Changed?.Invoke();
    }

    private async Task PersistAsync(ExternalSnapshot snapshot, CancellationToken ct)
    {
        var sessions = string.Join(',', snapshot.AllowedSessions.Select(s => s.Trim()).Where(s => s.Length > 0));

        await _settings.SetAsync(AllowSendKey, snapshot.AllowSend ? "true" : "false", ct).ConfigureAwait(false);
        await _settings.SetAsync(AllowedSessionsKey, sessions, ct).ConfigureAwait(false);
        await _settings.SetAsync(
            ReverseServersKey,
            JsonSerializer.Serialize(snapshot.ReverseServers.Select(s => new StoredReverseServer(
                s.Name, s.Host, s.Port, Encrypt(s.Token), null, s.Enabled))),
            ct).ConfigureAwait(false);
        await _settings.SetAsync(
            ForwardServersKey,
            JsonSerializer.Serialize(snapshot.ForwardServers.Select(s => new StoredForwardServer(
                s.Name, s.Url, Encrypt(s.Token), null, s.Enabled))),
            ct).ConfigureAwait(false);
    }

    private (IReadOnlyList<ExternalReverseServer> Servers, bool Migrated) ParseReverse(
        string? json, IReadOnlyList<ExternalReverseServer> fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (fallback, false);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredReverseServer>>(json);
            if (stored is null)
            {
                return (fallback, false);
            }

            var migrated = false;
            var servers = stored.Select(s =>
            {
                var (token, wasPlaintext) = ResolveToken(s.TokenCipher, s.Token);
                migrated |= wasPlaintext;

                return new ExternalReverseServer
                {
                    Name = s.Name,
                    Host = s.Host,
                    Port = s.Port,
                    Token = token,
                    Enabled = s.Enabled,
                };
            }).ToList();

            return (servers, migrated);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "解析 {Key} 失败，沿用启动配置", ReverseServersKey);
            return (fallback, false);
        }
    }

    private (IReadOnlyList<ExternalForwardServer> Servers, bool Migrated) ParseForward(
        string? json, IReadOnlyList<ExternalForwardServer> fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (fallback, false);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredForwardServer>>(json);
            if (stored is null)
            {
                return (fallback, false);
            }

            var migrated = false;
            var servers = stored.Select(s =>
            {
                var (token, wasPlaintext) = ResolveToken(s.TokenCipher, s.Token);
                migrated |= wasPlaintext;

                return new ExternalForwardServer
                {
                    Name = s.Name,
                    Url = s.Url,
                    Token = token,
                    Enabled = s.Enabled,
                };
            }).ToList();

            return (servers, migrated);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "解析 {Key} 失败，沿用启动配置", ForwardServersKey);
            return (fallback, false);
        }
    }

    private string? Encrypt(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : _protector.Protect(token.Trim());

    /// <summary>密文优先；没有密文时把旧版明文当值，并标记需要迁移。</summary>
    private (string? Token, bool WasPlaintext) ResolveToken(string? cipher, string? legacyPlaintext)
    {
        if (!string.IsNullOrWhiteSpace(cipher))
        {
            return (_protector.Unprotect(cipher), false);
        }

        if (!string.IsNullOrWhiteSpace(legacyPlaintext))
        {
            return (legacyPlaintext.Trim(), true);
        }

        return (null, false);
    }

    /// <summary>落库形状：token 只有密文；<c>Token</c> 只为兼容旧明文数据而保留（新写入恒为 null）。</summary>
    private sealed record StoredReverseServer(
        string Name, string Host, int Port, string? TokenCipher, string? Token, bool Enabled);

    private sealed record StoredForwardServer(
        string Name, string Url, string? TokenCipher, string? Token, bool Enabled);
}
