using Serilog;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Security;

/// <summary>
/// 执行策略（#28）：默认值、KV 覆盖、刷新、硬底线。
/// </summary>
public sealed class ExecutionPolicyTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private sealed class MemorySettings : IRuntimeSettingsStore
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    private static ExecutionPolicy Create(MemorySettings settings) =>
        new(settings, ToolCatalog.All, Logger);

    [Fact]
    public void Unconfigured_items_use_code_defaults()
    {
        var policy = Create(new MemorySettings());

        Assert.True(policy.IsEnabled(PolicyCategory.Tool, "send_message"));
        Assert.Equal(Role.Normal, policy.MinRole(PolicyCategory.Tool, "send_message", Role.Normal));
        Assert.Equal(Role.Admin, policy.MinRole(PolicyCategory.Tool, "file_delete", Role.Admin));
    }

    [Fact]
    public async Task Reload_applies_kv_overrides()
    {
        var settings = new MemorySettings();
        settings.Values[ExecutionPolicy.BuildKey(PolicyCategory.Tool, "send_message", "enabled")] = "0";
        settings.Values[ExecutionPolicy.BuildKey(PolicyCategory.Tool, "file_read", "role")] = "Admin";

        var policy = Create(settings);
        await policy.ReloadAsync();

        Assert.False(policy.IsEnabled(PolicyCategory.Tool, "send_message", true));
        Assert.Equal(Role.Admin, policy.MinRole(PolicyCategory.Tool, "file_read", Role.Whitelist));
    }

    [Fact]
    public async Task Missing_keys_fall_back_to_defaults_after_reload()
    {
        var policy = Create(new MemorySettings());
        await policy.ReloadAsync();

        Assert.True(policy.IsEnabled(PolicyCategory.Tool, "file_write", true));
        Assert.Equal(Role.Whitelist, policy.MinRole(PolicyCategory.Tool, "file_write", Role.Whitelist));
    }

    [Fact]
    public void Invalid_role_string_falls_back()
    {
        var settings = new MemorySettings();
        settings.Values[ExecutionPolicy.BuildKey(PolicyCategory.Command, "purge", "role")] = "GodMode";

        var policy = Create(settings);

        Assert.Equal(Role.Owner, policy.MinRole(PolicyCategory.Command, "purge", Role.Owner));
    }

    [Fact]
    public void Dangerous_tools_declare_hard_floor()
    {
        var delete = ToolCatalog.All.Single(i => i.Key == "file_delete");
        Assert.Equal(Role.Admin, delete.HardFloor);

        var send = ToolCatalog.All.Single(i => i.Key == "send_message");
        Assert.Null(send.HardFloor);
    }
}
