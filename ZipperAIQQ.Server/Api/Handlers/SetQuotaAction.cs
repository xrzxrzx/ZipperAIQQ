using System.Text.Json;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Billing;
using ZipperAIQQ.Server.Dto;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>修改额度配置（运行时生效，不必重启）。</summary>
public sealed class SetQuotaAction : IApiAction
{
    private readonly QuotaOptionsService _options;

    public SetQuotaAction(QuotaOptionsService options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string Name => "setQuota";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 必须是对象：{enabled, window, limitCny, warnRatio}");
        }

        await _options.EnsureLoadedAsync(ct).ConfigureAwait(false);
        var current = _options.Current;

        var next = current with
        {
            Enabled = payload.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? enabled.GetBoolean()
                : current.Enabled,
            Window = payload.TryGetProperty("window", out var window) && window.ValueKind == JsonValueKind.String
                ? window.GetString() ?? current.Window
                : current.Window,
            LimitCny = payload.TryGetProperty("limitCny", out var limit) && limit.TryGetDecimal(out var l) ? l : current.LimitCny,
            WarnRatio = payload.TryGetProperty("warnRatio", out var ratio) && ratio.TryGetDecimal(out var r) ? r : current.WarnRatio,
        };

        var error = await _options.UpdateAsync(next, ct).ConfigureAwait(false);
        if (error is not null)
        {
            throw new ApiException(ApiResponse.BadRequest, error);
        }

        var saved = _options.Current;
        return new
        {
            enabled = saved.Enabled,
            window = saved.Window,
            limitCny = saved.LimitCny,
            warnRatio = saved.WarnRatio,
        };
    }
}
