using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Server.Alerts;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Storage.Billing;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Server.Billing;

/// <summary>
/// 余额采样：每 N 分钟打一次 <c>/user/balance</c>，落一条曲线。
/// </summary>
/// <remarks>
/// <para>
/// 只对 **DeepSeek** 端点生效（其它供应商没有同形态接口）；端点不是 deepseek 时静默跳过，
/// 不让「采样失败」变成启动噪音。
/// </para>
/// <para>
/// 采样本身失败只 warn：余额是**观测**，不能因为它挂了就影响对话。
/// </para>
/// </remarks>
public sealed class BalanceMonitor : BackgroundService
{
    private readonly IConfigStore _store;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IBalanceStore _balance;
    private readonly BillingOptions _options;
    private readonly IAlertSink? _alerts;
    private readonly ILogger _logger;

    public BalanceMonitor(
        IConfigStore store,
        ISecretProtector protector,
        IHttpClientFactory httpFactory,
        IBalanceStore balance,
        BillingOptions options,
        ILogger logger,
        IAlertSink? alerts = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(balance);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _protector = protector;
        _httpFactory = httpFactory;
        _balance = balance;
        _options = options;
        _logger = logger;
        _alerts = alerts;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.BalanceEnabled)
        {
            _logger.Debug("余额采样已关闭（billing.balanceEnabled=false）");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.SampleMinutes));
        _logger.Information("余额采样启动：每 {Minutes} 分钟一次", interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "余额采样失败（不影响对话）");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SampleOnceAsync(CancellationToken ct)
    {
        var roles = await _store.ListRolesAsync(ct).ConfigureAwait(false);
        var chatRole = roles.FirstOrDefault(r => r.Role.Equals("Chat", StringComparison.OrdinalIgnoreCase));
        if (chatRole is null)
        {
            return;
        }

        var endpoint = await _store.GetEndpointAsync(chatRole.EndpointName, ct).ConfigureAwait(false);
        if (endpoint is null ||
            !endpoint.BaseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            return;   // 非 DeepSeek：没有同形态接口，静默跳过
        }

        var key = _protector.Unprotect(endpoint.KeyCipher);
        if (string.IsNullOrEmpty(key))
        {
            _logger.Warning("余额采样：端点 {Name} 的 key 解不开，跳过", endpoint.Name);
            return;
        }

        var sample = await DeepSeekBalanceClient
            .FetchAsync(_httpFactory.CreateClient(nameof(BalanceMonitor)), endpoint.BaseUrl, key, ct)
            .ConfigureAwait(false);

        await _balance.AppendAsync(sample, ct).ConfigureAwait(false);
        _logger.Debug("余额采样：{Total} {Currency}（available={Available}）",
            sample.Total, sample.Currency, sample.IsAvailable);

        if (!sample.IsAvailable)
        {
            _ = _alerts?.RaiseAsync(new AlertMessage(
                AlertKind.QuotaHardStop,
                DeepSeekBalanceClient.Provider,
                $"🛑 余额不足：{sample.Total:F2} {sample.Currency}（供应商自报不可用）",
                BeijingTime.Now), CancellationToken.None);
        }
    }
}
