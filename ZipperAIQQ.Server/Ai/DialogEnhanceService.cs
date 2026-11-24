using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// 对话增强开关的读写（问题记录 06 T2.6）。
/// </summary>
/// <remarks>
/// <para>
/// 存 <c>RuntimeSettings</c> 而不是 YAML：这四个开关是「改完要立刻生效」的运行期旋钮，
/// 与回复模式同类；放进启动配置就得重启，而用户调参数时最烦的就是重启。
/// </para>
/// <para>
/// ⚠️ **一个键存四个值**（<c>ai.dialogEnhance</c> = JSON），不是四个键各写一次。
/// 四个键的话，中途某个键写失败会留下「库里两开两关、内存全关」的半套配置，
/// 而这个分裂要等重启才暴露 —— 恰好是最难查的一类。单键一写天然原子。
/// </para>
/// <para>
/// 老版本用四个 <c>ai.dialogEnhance.*</c> 键；读的时候会兼容并顺手迁移到单键（只读不删老键）。
/// </para>
/// </remarks>
public sealed class DialogEnhanceService
{
    /// <summary>单键（JSON）：<c>{"webSearch":true,"readUrl":false,...}</c>。</summary>
    public const string StorageKey = "ai.dialogEnhance";

    // ── 老键（2026-11-24 之前的四键写法，仅用于迁移读取）──
    private const string LegacyWebSearchKey = "ai.dialogEnhance.webSearch";
    private const string LegacyReadUrlKey = "ai.dialogEnhance.readUrl";
    private const string LegacySafetyCheckKey = "ai.dialogEnhance.safetyCheck";
    private const string LegacyTitleKey = "ai.dialogEnhance.title";

    private readonly IRuntimeSettingsStore _settings;
    private readonly ILogger _logger;

    private volatile DeepSeekEnhanceOptions _current = new();

    public DialogEnhanceService(IRuntimeSettingsStore settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _logger = logger;
    }

    /// <summary>当前生效的开关（供 <c>ChatClientProvider</c> 建客户端时读取）。</summary>
    public DeepSeekEnhanceOptions Current => _current;

    /// <summary>从库里读回开关（启动时调一次）。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var raw = await _settings.GetAsync(StorageKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            _current = Parse(raw);
            _logger.Information("对话增强配置：{Summary}", _current.Describe());
            return;
        }

        // 兼容老四键：有任何一个存在就按它拼一份，并迁移到单键（**不删老键**，留人工恢复）
        var legacy = new DeepSeekEnhanceOptions
        {
            WebSearch = await ReadLegacyFlagAsync(LegacyWebSearchKey, ct).ConfigureAwait(false),
            ReadUrl = await ReadLegacyFlagAsync(LegacyReadUrlKey, ct).ConfigureAwait(false),
            SafetyCheckHigh = await ReadLegacyFlagAsync(LegacySafetyCheckKey, ct).ConfigureAwait(false),
            Title = await ReadLegacyFlagAsync(LegacyTitleKey, ct).ConfigureAwait(false),
        };

        _current = legacy;

        if (legacy.AnyEnabled)
        {
            await _settings.SetAsync(StorageKey, Serialize(legacy), ct).ConfigureAwait(false);
            _logger.Information(
                "已把老的四个对话增强键迁移为单键 {Key}（老键保留不删）：{Summary}",
                StorageKey, legacy.Describe());
        }
        else
        {
            _logger.Information("对话增强配置：{Summary}", legacy.Describe());
        }
    }

    /// <summary>写入四个开关（全量覆盖，**一次写库**），返回是否真的发生了变化。</summary>
    public async Task<bool> UpdateAsync(
        bool webSearch, bool readUrl, bool safetyCheckHigh, bool title, CancellationToken ct = default)
    {
        var next = new DeepSeekEnhanceOptions
        {
            WebSearch = webSearch,
            ReadUrl = readUrl,
            SafetyCheckHigh = safetyCheckHigh,
            Title = title,
        };

        if (next == _current)
        {
            return false;
        }

        await _settings.SetAsync(StorageKey, Serialize(next), ct).ConfigureAwait(false);

        _current = next;
        _logger.Warning("对话增强配置已变更：{Summary}", next.Describe());
        return true;
    }

    private static string Serialize(DeepSeekEnhanceOptions options) => JsonSerializer.Serialize(new
    {
        webSearch = options.WebSearch,
        readUrl = options.ReadUrl,
        safetyCheck = options.SafetyCheckHigh,
        title = options.Title,
    });

    /// <summary>解析单键；坏值按「全关」处理并记日志（宁可少开，不可意外多开）。</summary>
    private DeepSeekEnhanceOptions Parse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            return new DeepSeekEnhanceOptions
            {
                WebSearch = ReadBool(root, "webSearch"),
                ReadUrl = ReadBool(root, "readUrl"),
                SafetyCheckHigh = ReadBool(root, "safetyCheck"),
                Title = ReadBool(root, "title"),
            };
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "对话增强配置无法解析，已按全关处理：{Raw}", raw);
            return new DeepSeekEnhanceOptions();
        }
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var node) &&
        node.ValueKind == JsonValueKind.True;

    private async Task<bool> ReadLegacyFlagAsync(string key, CancellationToken ct)
    {
        var raw = await _settings.GetAsync(key, ct).ConfigureAwait(false);
        return SettingsValue.IsTrue(raw);
    }
}
