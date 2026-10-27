using Serilog;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Channels;

/// <summary>抖音 sidecar 的**可热更新**配置快照（决策七：客户端要能改）。</summary>
public sealed record DouyinSnapshot(
    bool Enabled,
    string? Token,
    IReadOnlyList<string> AllowedSessions,
    int SendTimeoutMs,
    int MaxTextLength,
    int QueryTimeoutMs)
{
    /// <summary>转成通道用的不可变配置。</summary>
    public DouyinOptions ToOptions() => new()
    {
        Enabled = Enabled,
        Token = Token,
        AllowedSessions = AllowedSessions,
        SendTimeoutMs = SendTimeoutMs,
        MaxTextLength = MaxTextLength,
        QueryTimeoutMs = QueryTimeoutMs,
    };
}

/// <summary>
/// 抖音 sidecar 配置（客户端可改）：优先读 <c>RuntimeSettings</c>，没有则用启动配置。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="External.ExternalOptionsService"/> 同一套路（决策七要求"参考 WS 通道的配置逻辑"）：
/// 启动时灌一次 → 客户端保存就写库 + 立刻生效 → 读的人只看 <see cref="Current"/>。
/// </para>
/// <para>
/// <b>token 加密落库</b>（<c>douyin.tokenCipher</c>）：沿用 LLM key 的做法（Data Protection）。
/// 客户端只看到 <c>tokenConfigured</c>，明文永不回显、永不进日志。
/// </para>
/// <para>
/// 改了 <see cref="Enabled"/> 不会热启停端点（端点每次请求都读快照，所以下一次连接就生效）；
/// 改了白名单/超时/长度则通过 <see cref="Changed"/> 推给 <see cref="DouyinChannel"/>。
/// </para>
/// </remarks>
public sealed class DouyinOptionsService
{
    public const string EnabledKey = "douyin.enabled";
    public const string TokenCipherKey = "douyin.tokenCipher";
    public const string AllowedSessionsKey = "douyin.allowedSessions";
    public const string SendTimeoutKey = "douyin.sendTimeoutMs";
    public const string MaxTextLengthKey = "douyin.maxTextLength";
    public const string QueryTimeoutKey = "douyin.queryTimeoutMs";

    private readonly IRuntimeSettingsStore _settings;
    private readonly ServerOptions _bootstrap;
    private readonly ISecretProtector _protector;
    private readonly ILogger _logger;

    private volatile DouyinSnapshot _current;

    public DouyinOptionsService(
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

        var d = bootstrap.Douyin;
        _current = new DouyinSnapshot(
            d.Enabled, d.Token, d.AllowedSessions, d.SendTimeoutMs, d.MaxTextLength, d.QueryTimeoutMs);
    }

    /// <summary>当前生效的配置。</summary>
    public DouyinSnapshot Current => _current;

    /// <summary>配置变更（通道据此刷新白名单 / 超时）。</summary>
    public event Action? Changed;

    /// <summary>从库里读回配置；没有的键沿用启动配置（升级前部署零迁移）。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var enabledRaw = await _settings.GetAsync(EnabledKey, ct).ConfigureAwait(false);
        var cipher = await _settings.GetAsync(TokenCipherKey, ct).ConfigureAwait(false);
        var sessionsRaw = await _settings.GetAsync(AllowedSessionsKey, ct).ConfigureAwait(false);
        var sendRaw = await _settings.GetAsync(SendTimeoutKey, ct).ConfigureAwait(false);
        var lengthRaw = await _settings.GetAsync(MaxTextLengthKey, ct).ConfigureAwait(false);
        var queryRaw = await _settings.GetAsync(QueryTimeoutKey, ct).ConfigureAwait(false);

        var bootstrap = _bootstrap.Douyin;

        var token = string.IsNullOrWhiteSpace(cipher) ? bootstrap.Token : _protector.Unprotect(cipher);
        if (!string.IsNullOrWhiteSpace(cipher) && token is null)
        {
            // 主密钥换了：解不开就退回启动配置（若也没有 = 没配置），并明确告警
            _logger.Warning("抖音 token 解不开（主密钥可能已变更），回退到启动配置的值");
            token = bootstrap.Token;
        }

        _current = new DouyinSnapshot(
            Enabled: enabledRaw is null ? bootstrap.Enabled : IsTrue(enabledRaw),
            Token: token,
            AllowedSessions: sessionsRaw is null ? bootstrap.AllowedSessions : SplitList(sessionsRaw),
            SendTimeoutMs: ParseInt(sendRaw, bootstrap.SendTimeoutMs),
            MaxTextLength: ParseInt(lengthRaw, bootstrap.MaxTextLength),
            QueryTimeoutMs: ParseInt(queryRaw, bootstrap.QueryTimeoutMs));

        _logger.Information(
            "抖音配置已加载：启用={Enabled} token={TokenState} 白名单 {Count} 条",
            _current.Enabled ? "是" : "否",
            string.IsNullOrWhiteSpace(_current.Token) ? "未配置" : "已配置",
            _current.AllowedSessions.Count);
    }

    /// <summary>
    /// 写入配置并立即生效。
    /// </summary>
    /// <param name="enabled">是否启用；<c>null</c> = 不改。</param>
    /// <param name="token">
    /// 新 token；<c>null</c> = 不改，<c>""</c> = 清除，其它 = 加密后覆盖。
    /// </param>
    /// <param name="allowedSessions"><c>null</c> = 不改。</param>
    /// <param name="sendTimeoutMs"><c>null</c> = 不改。</param>
    /// <param name="maxTextLength"><c>null</c> = 不改。</param>
    /// <param name="queryTimeoutMs"><c>null</c> = 不改。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际改动的键（供审计与回显）。</returns>
    public async Task<IReadOnlyList<string>> UpdateAsync(
        bool? enabled,
        string? token,
        IReadOnlyList<string>? allowedSessions,
        int? sendTimeoutMs,
        int? maxTextLength,
        int? queryTimeoutMs,
        CancellationToken ct = default)
    {
        var changed = new List<string>();
        var current = _current;

        var nextEnabled = current.Enabled;
        if (enabled is { } e && e != current.Enabled)
        {
            await _settings.SetAsync(EnabledKey, e ? "1" : "0", ct).ConfigureAwait(false);
            nextEnabled = e;
            changed.Add("enabled");
        }

        var nextToken = current.Token;
        if (token is not null)
        {
            if (token.Length == 0)
            {
                await _settings.SetAsync(TokenCipherKey, string.Empty, ct).ConfigureAwait(false);
                nextToken = null;
                changed.Add("token(cleared)");
            }
            else
            {
                await _settings.SetAsync(TokenCipherKey, _protector.Protect(token), ct).ConfigureAwait(false);
                nextToken = token;
                changed.Add("token");
            }
        }

        var nextSessions = current.AllowedSessions;
        if (allowedSessions is not null)
        {
            var normalized = allowedSessions
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await _settings
                .SetAsync(AllowedSessionsKey, string.Join(',', normalized), ct)
                .ConfigureAwait(false);

            nextSessions = normalized;
            changed.Add($"allowedSessions={normalized.Count}");
        }

        var nextSend = current.SendTimeoutMs;
        if (sendTimeoutMs is { } s && s > 0 && s != current.SendTimeoutMs)
        {
            await _settings.SetAsync(SendTimeoutKey, s.ToString(), ct).ConfigureAwait(false);
            nextSend = s;
            changed.Add("sendTimeoutMs");
        }

        var nextLength = current.MaxTextLength;
        if (maxTextLength is { } l && l > 0 && l != current.MaxTextLength)
        {
            await _settings.SetAsync(MaxTextLengthKey, l.ToString(), ct).ConfigureAwait(false);
            nextLength = l;
            changed.Add("maxTextLength");
        }

        var nextQuery = current.QueryTimeoutMs;
        if (queryTimeoutMs is { } q && q > 0 && q != current.QueryTimeoutMs)
        {
            await _settings.SetAsync(QueryTimeoutKey, q.ToString(), ct).ConfigureAwait(false);
            nextQuery = q;
            changed.Add("queryTimeoutMs");
        }

        if (changed.Count == 0)
        {
            return changed;
        }

        _current = new DouyinSnapshot(nextEnabled, nextToken, nextSessions, nextSend, nextLength, nextQuery);

        _logger.Information("抖音配置已更新：{Changed}", string.Join(",", changed));
        Changed?.Invoke();

        return changed;
    }

    private static bool IsTrue(string raw) => raw is "1" or "true" or "True";

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, out var value) && value > 0 ? value : fallback;

    /// <summary>逗号分隔的列表（与其它配置节同口径）。</summary>
    private static IReadOnlyList<string> SplitList(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
