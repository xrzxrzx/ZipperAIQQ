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

    private readonly IRuntimeSettingsStore _settings;
    private readonly MailOptions _bootstrap;
    private readonly ILogger _logger;

    private volatile bool _enabled;

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
    }

    /// <summary>当前是否启用。</summary>
    public bool Enabled => _enabled;

    /// <summary>开关变更（管道据此启停邮件源）。</summary>
    public event Action? Changed;

    /// <summary>从库里读回开关；没有这个键就沿用启动配置。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var raw = await _settings.GetAsync(EnabledKey, ct).ConfigureAwait(false);
        _enabled = raw is null ? _bootstrap.Enabled : raw is "1" or "true" or "True";

        _logger.Information("邮件接入开关已加载：{State}（来源：{Source}）",
            _enabled ? "启用" : "停用",
            raw is null ? "启动配置" : "客户端配置");
    }

    /// <summary>
    /// 设置开关并落库。
    /// </summary>
    /// <returns>是否真的发生了变化（<c>false</c> = 值没变，不算错）。</returns>
    public async Task<bool> SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (_enabled == enabled)
        {
            return false;
        }

        await _settings.SetAsync(EnabledKey, enabled ? "1" : "0", ct).ConfigureAwait(false);
        _enabled = enabled;

        _logger.Information("邮件接入已{State}（客户端配置）", enabled ? "启用" : "停用");
        Changed?.Invoke();

        return true;
    }
}
