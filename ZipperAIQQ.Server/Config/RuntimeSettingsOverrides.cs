using Microsoft.Data.Sqlite;
using Serilog;
using ZipperAIQQ.Server.Configuration;

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
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            async Task<double?> ReadAsync(string key)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Value FROM RuntimeSettings WHERE Key = @key;";
                cmd.Parameters.AddWithValue("@key", key);

                var value = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                if (value is null or DBNull)
                {
                    return null;
                }

                return double.TryParse(
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
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

            var merged = options with
            {
                SendIntervalMs = (int)(interval ?? options.SendIntervalMs),
                MaxConsecutive = (int)(maxConsecutive ?? options.MaxConsecutive),
                CooldownMs = (int)(cooldown ?? options.CooldownMs),
                FileMaxBytes = (int)(fileMax ?? options.FileMaxBytes),
                HotWindowMessages = (int)(hotWindow ?? options.HotWindowMessages),
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
}
