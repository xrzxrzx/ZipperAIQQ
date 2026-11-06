using Serilog;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Billing;

/// <summary>读当前额度配置（运行时可改）。</summary>
public interface IQuotaOptionsProvider
{
    QuotaOptions Current { get; }

    /// <summary>确保已从运行时配置加载过（首次调用会读库）。</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);
}

/// <summary>
/// 额度配置的运行时快照：启动值来自 <c>server.yaml</c>，之后可被客户端 <c>setQuota</c> 覆盖。
/// </summary>
/// <remarks>
/// 为什么不做成「改完等重启」：11-05 那种事故是**当天**发生的，
/// 等人去改 yaml + 重启，钱早烧完了。热更新是这条闸门的意义所在。
/// </remarks>
public sealed class QuotaOptionsService : IQuotaOptionsProvider
{
    public const string EnabledKey = "quota.enabled";
    public const string WindowKey = "quota.window";
    public const string LimitKey = "quota.limitCny";
    public const string WarnRatioKey = "quota.warnRatio";

    private static readonly string[] Windows = ["hour", "day", "month"];

    private readonly IRuntimeSettingsStore _settings;
    private readonly QuotaOptions _startup;
    private readonly ILogger _logger;

    private volatile QuotaOptions _current;
    private int _loaded;

    public QuotaOptionsService(IRuntimeSettingsStore settings, QuotaOptions startup, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _startup = startup;
        _logger = logger;
        _current = startup;
    }

    public QuotaOptions Current => _current;

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _loaded, 1) == 1)
        {
            return;
        }

        _current = await ReadAsync(ct).ConfigureAwait(false);
        _logger.Information(
            "额度配置已加载：enabled={Enabled} window={Window} limit={Limit} warn={Warn}",
            _current.Enabled, _current.Window, _current.LimitCny, _current.WarnRatio);
    }

    /// <summary>校验并持久化；返回错误信息（<c>null</c> = 成功）。</summary>
    public async Task<string?> UpdateAsync(QuotaOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.LimitCny <= 0)
        {
            return "额度上限必须大于 0。";
        }

        if (options.WarnRatio <= 0 || options.WarnRatio >= 1)
        {
            return "预警比例必须在 0~1 之间（不含端点）。";
        }

        var window = options.Window?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!Windows.Contains(window))
        {
            return "窗口只能是 hour / day / month。";
        }

        var normalized = options with { Window = window };

        await _settings.SetAsync(EnabledKey, normalized.Enabled ? "true" : "false", ct).ConfigureAwait(false);
        await _settings.SetAsync(WindowKey, normalized.Window, ct).ConfigureAwait(false);
        await _settings.SetAsync(LimitKey, normalized.LimitCny.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await _settings.SetAsync(WarnRatioKey, normalized.WarnRatio.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        _current = normalized;
        return null;
    }

    private async Task<QuotaOptions> ReadAsync(CancellationToken ct)
    {
        var enabled = await _settings.GetAsync(EnabledKey, ct).ConfigureAwait(false);
        var window = await _settings.GetAsync(WindowKey, ct).ConfigureAwait(false);
        var limit = await _settings.GetAsync(LimitKey, ct).ConfigureAwait(false);
        var warn = await _settings.GetAsync(WarnRatioKey, ct).ConfigureAwait(false);

        return _startup with
        {
            Enabled = enabled is null ? _startup.Enabled : enabled.Equals("true", StringComparison.OrdinalIgnoreCase),
            Window = string.IsNullOrWhiteSpace(window) ? _startup.Window : window,
            LimitCny = decimal.TryParse(limit, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : _startup.LimitCny,
            WarnRatio = decimal.TryParse(warn, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : _startup.WarnRatio,
        };
    }
}
