using System.Text.Json;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 写入数值型运行参数（M2-E）：**逐项按 schema 校验**，越界整体拒绝。
/// </summary>
/// <remarks>
/// payload 形状：<c>{ "settings": { "send.intervalMs": 300, "context.hotWindow": 20 } }</c>
/// <para>
/// 校验在服务端做 —— UI 的 NumberBox 只是引导，真正的边界不能被客户端绕过。
/// </para>
/// </remarks>
public sealed class SetSettingsAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IConfigStore _configs;

    public SetSettingsAction(IRuntimeSettingsStore settings, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configs);

        _settings = settings;
        _configs = configs;
    }

    public string Name => "setSettings";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("settings", out var settings) ||
            settings.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 { settings: { key: value } }。");
        }

        // 先全部校验，再统一写入 —— 不做"写一半失败"，避免参数处在半套状态
        var pending = new List<(string Key, double Value)>();

        foreach (var item in settings.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.Number || !item.Value.TryGetDouble(out var value))
            {
                throw new ApiException(ApiResponse.BadRequest, $"{item.Name} 必须是数值。");
            }

            if (!NumericSettingsSchema.TryValidate(item.Name, value, out var error))
            {
                throw new ApiException(ApiResponse.BadRequest, error!);
            }

            pending.Add((item.Name, value));
        }

        foreach (var (key, value) in pending)
        {
            await _settings.SetAsync(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), ct)
                .ConfigureAwait(false);
        }

        await _configs
            .AppendAuditAsync("setSettings", Actor, string.Join(",", pending.Select(p => p.Key)), ct)
            .ConfigureAwait(false);

        return new { updated = pending.Count, keys = pending.Select(p => p.Key).ToList() };
    }
}
