using Serilog;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.External;
using ZipperAIQQ.Server.Identity;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Config;

/// <summary>
/// 启动时把 <c>RuntimeSettings</c> 里的数值参数覆盖到引导配置上。
/// </summary>
/// <remarks>
/// <para>
/// 为什么在**启动时**读而不是每轮读：这些是装配期参数（送件限速、热窗大小等），
/// 热改会让运行中的 agent 处在半新半旧的状态。改完重启生效，语义清楚。
/// </para>
/// <para>
/// 读失败一律退回环境变量值 —— 配置读不出来不该让服务起不来。
/// </para>
/// </remarks>
public static class RuntimeSettingsOverrides
{
    public static async Task<ServerOptions> ApplyAsync(
        ServerOptions options, string connectionString, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await using var db = new SqliteDatabase(connectionString, logger);
            var settings = new SqliteRuntimeSettingsStore(db);

            async Task<double?> ReadAsync(string key)
            {
                var raw = await settings.GetAsync(key).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                return double.TryParse(
                    raw,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : null;
            }

            var interval = await ReadAsync(NumericSettingsSchema.SendIntervalKey).ConfigureAwait(false);
            var maxConsecutive = await ReadAsync(NumericSettingsSchema.SendMaxConsecutiveKey).ConfigureAwait(false);
            var cooldown = await ReadAsync(NumericSettingsSchema.SendCooldownKey).ConfigureAwait(false);
            var fileMax = await ReadAsync(NumericSettingsSchema.FileMaxBytesKey).ConfigureAwait(false);
            var hotWindow = await ReadAsync(NumericSettingsSchema.HotWindowKey).ConfigureAwait(false);

            var persistedSelfId = await settings.GetAsync(SelfIdRecorder.StorageKey).ConfigureAwait(false);

            var merged = options with
            {
                SendIntervalMs = (int)(interval ?? options.SendIntervalMs),
                MaxConsecutive = (int)(maxConsecutive ?? options.MaxConsecutive),
                CooldownMs = (int)(cooldown ?? options.CooldownMs),
                FileMaxBytes = (int)(fileMax ?? options.FileMaxBytes),
                HotWindowMessages = (int)(hotWindow ?? options.HotWindowMessages),
                SelfId = string.IsNullOrWhiteSpace(options.SelfId) ? persistedSelfId : options.SelfId,
                ExternalReverseServers = await ReadReverseServersAsync(settings, options, logger).ConfigureAwait(false),
            };

            if (interval is not null || hotWindow is not null || fileMax is not null)
            {
                logger.Information(
                    "运行参数已覆盖：发送间隔={Interval}ms 连发={Max}/{Cooldown}ms 文件上限={File} 热窗={Hot}",
                    merged.SendIntervalMs, merged.MaxConsecutive, merged.CooldownMs, merged.FileMaxBytes,
                    merged.HotWindowMessages);
            }

            return merged;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "读取运行参数失败，沿用环境变量里的值");
            return options;
        }
    }

    /// <summary>读取反向 WS 监听块（客户端保存的 JSON）；失败沿用启动配置。</summary>
    private static async Task<IReadOnlyList<ExternalReverseServer>> ReadReverseServersAsync(
        IRuntimeSettingsStore settings, ServerOptions options, ILogger logger)
    {
        var json = await settings
            .GetAsync(ExternalOptionsService.ReverseServersKey)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(json))
        {
            return options.ExternalReverseServers;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<ExternalReverseServer>>(json)
                   ?? options.ExternalReverseServers;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "解析 external.reverseServers 失败，沿用启动配置");
            return options.ExternalReverseServers;
        }
    }
}
