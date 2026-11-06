using Serilog;
using ZipperAIQQ.Server.Billing;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>额度配置热更新：启动值 → 运行时覆盖 → 校验。</summary>
public sealed class QuotaOptionsServiceTests
{
    private sealed class FakeSettings(Dictionary<string, string> values) : IRuntimeSettingsStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string? value, CancellationToken ct = default)
        {
            if (value is null)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }

            return Task.CompletedTask;
        }
    }

    private static QuotaOptionsService Create(Dictionary<string, string> values, QuotaOptions? startup = null) =>
        new(new FakeSettings(values),
            startup ?? new QuotaOptions { Enabled = false, LimitCny = 5m },
            new LoggerConfiguration().CreateLogger());

    [Fact]
    public async Task Runtime_keys_override_startup_values()
    {
        var service = Create(new Dictionary<string, string>
        {
            [QuotaOptionsService.EnabledKey] = "true",
            [QuotaOptionsService.LimitKey] = "2.5",
            [QuotaOptionsService.WindowKey] = "hour",
        });

        await service.EnsureLoadedAsync();

        Assert.True(service.Current.Enabled);
        Assert.Equal(2.5m, service.Current.LimitCny);
        Assert.Equal("hour", service.Current.Window);
    }

    [Fact]
    public async Task Update_persists_and_swaps_snapshot()
    {
        var values = new Dictionary<string, string>();
        var service = Create(values);
        await service.EnsureLoadedAsync();

        var error = await service.UpdateAsync(new QuotaOptions
        {
            Enabled = true,
            Window = "day",
            LimitCny = 8m,
            WarnRatio = 0.8m,
        });

        Assert.Null(error);
        Assert.True(service.Current.Enabled);
        Assert.Equal("8", values[QuotaOptionsService.LimitKey]);
        Assert.Equal("true", values[QuotaOptionsService.EnabledKey]);
    }

    [Theory]
    [InlineData(0, 0.7, "day", "上限")]
    [InlineData(5, 1, "day", "预警")]
    [InlineData(5, 0.7, "week", "窗口")]
    public async Task Invalid_values_are_rejected(decimal limit, decimal ratio, string window, string keyword)
    {
        var service = Create([]);
        await service.EnsureLoadedAsync();

        var error = await service.UpdateAsync(new QuotaOptions
        {
            Enabled = true,
            Window = window,
            LimitCny = limit,
            WarnRatio = ratio,
        });

        Assert.NotNull(error);
        Assert.Contains(keyword, error);
    }
}
