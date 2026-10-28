using Serilog;
using ZipperAIQQ.Server.Mail;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Mail;

/// <summary>
/// 邮件接入的**运行时开关**（S3-F 补：客户端可配置，不必重启）。
/// </summary>
/// <remarks>
/// <para>
/// 关 = 不连 IMAP、不摘要、不转发；已经落库的邮件与账号配置**都保留**（重新打开就能继续处理待办）。
/// </para>
/// <para>
/// 为什么单独一个服务而不是直接读 <see cref="MailOptions"/>：后者是**启动配置**（YAML），
/// 改了必须重启；而用户要的是"客户端点一下就生效"。与抖音的
/// <see cref="ZipperAIQQ.Server.Channels.DouyinOptionsService"/>、外部 WS 的
/// <c>ExternalOptionsService</c> 同一套路：启动值兜底 + 库里优先 + 变更事件。
/// </para>
/// </remarks>
public sealed class MailOptionsService
{
    /// <summary>运行时开关的存储键。</summary>
    public const string EnabledKey = "mail.enabled";

    /// <summary>
    /// 「汇总再经人格润色」的存储键（2026-10-28 用户要求：做成可选项）。
    /// </summary>
    public const string PersonaDigestKey = "mail.personaDigest";

    private readonly IRuntimeSettingsStore _settings;
    private readonly MailOptions _bootstrap;
    private readonly ILogger _logger;

    private volatile bool _enabled;
    private volatile bool _personaDigest;

    public MailOptionsService(
        IRuntimeSettingsStore settings,
        MailOptions bootstrap,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _bootstrap = bootstrap;
        _logger = logger;

        // 库里还没写过之前，用启动配置（升级前部署零迁移）
        _enabled = bootstrap.Enabled;
        _personaDigest = bootstrap.PersonaDigest;
    }

    /// <summary>当前是否启用。</summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// 汇总是否**再经一遍人格 agent**（关掉 = 直接发汇总文本，省一次模型调用）。
    /// </summary>
    public bool PersonaDigest => _personaDigest;

    /// <summary>开关变更（管道据此启停邮件源）。</summary>
    public event Action? Changed;

    /// <summary>从库里读回开关；没有这个键就沿用启动配置。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var rawEnabled = await _settings.GetAsync(EnabledKey, ct).ConfigureAwait(false);
        _enabled = rawEnabled is null ? _bootstrap.Enabled : IsTrue(rawEnabled);

        var rawPersona = await _settings.GetAsync(PersonaDigestKey, ct).ConfigureAwait(false);
        _personaDigest = rawPersona is null ? _bootstrap.PersonaDigest : IsTrue(rawPersona);

        _logger.Information(
            "邮件配置已加载：接入={Enabled}（{EnabledSource}） / 人格润色={Persona}（{PersonaSource}）",
            _enabled ? "启用" : "停用",
            rawEnabled is null ? "启动配置" : "客户端配置",
            _personaDigest ? "开" : "关",
            rawPersona is null ? "启动配置" : "客户端配置");
    }

    /// <summary>
    /// 设置开关（接入 / 人格润色）并落库。<c>null</c> = 不改。
    /// </summary>
    /// <returns>实际改动的键；空列表 = 值没变（不算错）。</returns>
    public async Task<IReadOnlyList<string>> SetAsync(
        bool? enabled,
        bool? personaDigest,
        CancellationToken ct = default)
    {
        var changed = new List<string>();

        if (enabled is { } e && e != _enabled)
        {
            await _settings.SetAsync(EnabledKey, e ? "1" : "0", ct).ConfigureAwait(false);
            _enabled = e;
            changed.Add("enabled");
        }

        if (personaDigest is { } p && p != _personaDigest)
        {
            await _settings.SetAsync(PersonaDigestKey, p ? "1" : "0", ct).ConfigureAwait(false);
            _personaDigest = p;
            changed.Add("personaDigest");
        }

        if (changed.Count == 0)
        {
            return changed;
        }

        _logger.Information("邮件配置已更新：{Changed}", string.Join(",", changed));
        Changed?.Invoke();

        return changed;
    }

    private static bool IsTrue(string raw) => raw is "1" or "true" or "True";
}
