using System.Globalization;
using System.Text.Json;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>读取数值型运行参数：返回 schema（含范围）+ 当前值（M2-E）。</summary>
public sealed class GetSettingsAction : IApiAction
{
    private readonly IRuntimeSettingsStore _settings;

    public GetSettingsAction(IRuntimeSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string Name => "getSettings";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var items = new List<object>();

        foreach (var setting in NumericSettingsSchema.All)
        {
            var raw = await _settings.GetAsync(setting.Key, ct).ConfigureAwait(false);
            var current = double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : setting.Default;

            items.Add(new
            {
                key = setting.Key,
                label = setting.Label,
                min = setting.Min,
                max = setting.Max,
                @default = setting.Default,
                unit = setting.Unit,
                value = current,
                stored = raw is not null,
            });
        }

        return items;
    }
}
