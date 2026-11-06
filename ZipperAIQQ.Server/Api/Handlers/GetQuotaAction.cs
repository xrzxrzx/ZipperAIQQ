using System.Text.Json;
using ZipperAIQQ.Server.Billing;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>额度状态：配置 + 当前窗口用量 + 余额（客户端看板读这个）。</summary>
public sealed class GetQuotaAction : IApiAction
{
    private readonly IQuotaOptionsProvider _options;
    private readonly ISpendReader _spend;

    public GetQuotaAction(IQuotaOptionsProvider options, ISpendReader spend)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spend);

        _options = options;
        _spend = spend;
    }

    public string Name => "getQuota";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        await _options.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var options = _options.Current;
        var snapshot = await _spend.ReadAsync(ct).ConfigureAwait(false);

        return new
        {
            enabled = options.Enabled,
            window = options.Window,
            limitCny = options.LimitCny,
            warnRatio = options.WarnRatio,
            spentCny = snapshot.SpentCny,
            source = snapshot.Source,
            remainingCny = snapshot.RemainingCny,
            hasData = snapshot.HasData,
        };
    }
}
