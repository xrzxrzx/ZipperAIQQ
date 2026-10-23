using Serilog;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Agent.Security;

/// <summary>
/// <see cref="IExecutionPolicy"/> 的内存快照实现。
/// </summary>
/// <remarks>
/// <para>
/// 为什么用快照而不是每次查库：工具在**每条消息**都要构建一次，
/// 每条几十次 SQLite 读会把"回一句话"变成磁盘活动。
/// </para>
/// <para>
/// 刷新由 Server 的 <c>PolicyRefreshService</c>（定时）与 <c>setToolPolicy</c>（即时）触发。
/// 刷新时构造新字典再整体替换 —— 读方永远看到一致快照，不会读到"一半新一半旧"。
/// </para>
/// </remarks>
public sealed class ExecutionPolicy : IExecutionPolicy
{
    private readonly IRuntimeSettingsStore _settings;
    private readonly ILogger _logger;

    private volatile Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public ExecutionPolicy(
        IRuntimeSettingsStore settings,
        IEnumerable<PolicyItem> catalog,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _logger = logger;
        Catalog = catalog.ToList();
    }

    public IReadOnlyList<PolicyItem> Catalog { get; }

    public bool IsEnabled(PolicyCategory category, string key, bool fallback = true)
    {
        var raw = Get(category, key, "enabled");
        return raw is null ? fallback : raw is "1" or "true";
    }

    public Role MinRole(PolicyCategory category, string key, Role fallback)
    {
        var raw = Get(category, key, "role");
        return !string.IsNullOrWhiteSpace(raw) &&
               Enum.TryParse<Role>(raw, ignoreCase: true, out var role)
            ? role
            : fallback;
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var next = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in Catalog)
        {
            foreach (var suffix in new[] { "enabled", "role" })
            {
                var storageKey = BuildKey(item.Category, item.Key, suffix);
                var value = await _settings.GetAsync(storageKey, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    next[storageKey] = value;
                }
            }
        }

        _values = next;
        _logger.Debug("执行策略已刷新：{Count} 条配置", next.Count);
    }

    private string? Get(PolicyCategory category, string key, string suffix) =>
        _values.TryGetValue(BuildKey(category, key, suffix), out var value) ? value : null;

    /// <summary>KV 键：<c>policy.&lt;enabled|role&gt;:&lt;category&gt;:&lt;key&gt;</c>。</summary>
    public static string BuildKey(PolicyCategory category, string key, string suffix) =>
        $"policy.{suffix}:{CategoryKey(category)}:{key}";

    public static string CategoryKey(PolicyCategory category) => category switch
    {
        PolicyCategory.Tool => "tool",
        PolicyCategory.Command => "command",
        _ => "channelapi",
    };
}
