using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Express;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Express;

/// <summary>
/// 表达档位的读写与热更新（问题记录 06 B6 / 表达档位设计 §7）。
/// </summary>
/// <remarks>
/// <para>
/// 存 <c>RuntimeSettings</c> 而不是 YAML：档位是「改完要立刻生效」的运行期旋钮，
/// 与回复模式 / 对话增强同类；放进启动配置就得重启。
/// </para>
/// <para>
/// ⚠️ **一个键存整份档位**（<c>express</c> = JSON），不是五个键各写一次。
/// 多键写法的中途失败会留下「库里档位改了、群上限还是旧的」这种半套配置，
/// 而这种分裂要等下次对话才暴露（与 B2-c 的 <c>ai.dialogEnhance</c> 同一条理由）。
/// </para>
/// <para>
/// 规则段文本另存 <c>prompt.express</c>：它和 <c>prompt.system</c> 是同一类东西（可长文本、客户端编辑），
/// 塞进 JSON 里会让「恢复默认」与「编辑中」两件事互相踩。
/// </para>
/// </remarks>
public sealed class ExpressOptionsService : IExpressSettingsSource
{
    /// <summary>档位单键（JSON）。</summary>
    public const string StorageKey = "express";

    /// <summary>规则段覆盖文本（空 = 用内置默认）。</summary>
    public const string PromptKey = "prompt.express";

    private readonly IRuntimeSettingsStore _settings;
    private readonly ILogger _logger;

    /// <summary>写操作串行化：并发两次改档会互相覆盖，最后落库的那份才算数。</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private volatile ExpressSettings _current = new();

    private long _hygieneHits;
    private long _hygieneRegenerated;

    public ExpressOptionsService(IRuntimeSettingsStore settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _logger = logger;
    }

    /// <summary>当前生效快照（Agent 每轮读它算档位）。</summary>
    public ExpressSettings Current => _current;

    /// <summary>卫生命中总次数（进程内累计，重启归零 —— 它只是观测，不是配额）。</summary>
    public long HygieneHits => Interlocked.Read(ref _hygieneHits);

    /// <summary>免拒绝校准真正触发重生成的次数。</summary>
    public long HygieneRegenerated => Interlocked.Read(ref _hygieneRegenerated);

    /// <summary>记一次卫生命中。</summary>
    public void RecordHygieneHit(ExpressHygieneCategory category)
    {
        Interlocked.Increment(ref _hygieneHits);

        // 分类只在 Debug 里记：这是每轮都可能发生的事，抬到 Information 会把日志刷满
        _logger.Debug("免拒绝校准命中：{Category}", category);
    }

    /// <summary>记一次「已请模型重说一遍」。</summary>
    public void RecordHygieneRegeneration() => Interlocked.Increment(ref _hygieneRegenerated);

    /// <summary>从库里读回档位（启动时调一次）。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var raw = await _settings.GetAsync(StorageKey, ct).ConfigureAwait(false);
        var prompt = await _settings.GetAsync(PromptKey, ct).ConfigureAwait(false);

        _current = Parse(raw) with { CustomRules = string.IsNullOrWhiteSpace(prompt) ? null : prompt };

        _logger.Information(
            "表达档位：{Level}（群上限 {Cap}，会话覆盖 {Count} 个，免拒绝校准 {Hygiene}）",
            ExpressLevelText.ToLabel(_current.Level),
            ExpressLevelText.ToLabel(_current.GroupCap),
            _current.SessionOverrides.Count,
            _current.HygieneEnabled ? "开" : "关");
    }

    /// <summary>
    /// 写档位：只改给了的项，其它保持（避免「只想降群里上限，结果把全局档也重置了」）。
    /// </summary>
    /// <param name="level">全局档；<c>null</c> = 不改。</param>
    /// <param name="groupCap">群聊上限；<c>null</c> = 不改。</param>
    /// <param name="sessionId">会话覆盖的目标；<c>null</c> = 不动任何会话覆盖。</param>
    /// <param name="sessionLevel">
    /// 该会话的档位；传 <c>null</c> = **删除**这条覆盖（回到全局/上限）。
    /// </param>
    /// <param name="promptExpress">
    /// 规则段覆盖文本；<c>null</c> = 不改，空串 = 恢复内置默认。
    /// </param>
    /// <param name="hygieneEnabled">免拒绝校准总开关；<c>null</c> = 不改。</param>
    /// <param name="hygieneRegenerate">命中后是否重生成；<c>null</c> = 不改。</param>
    /// <param name="actor">操作者（写进 <c>updatedBy</c>）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际改动的字段名（供审计与回显）；空 = 没有任何变化。</returns>
    /// <exception cref="ArgumentException">档位值认不出。</exception>
    public async Task<IReadOnlyList<string>> UpdateAsync(
        ExpressLevel? level,
        ExpressLevel? groupCap,
        string? sessionId,
        ExpressLevel? sessionLevel,
        string? promptExpress,
        bool? hygieneEnabled,
        bool? hygieneRegenerate,
        string actor,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _current;
            var changed = new List<string>();

            var nextLevel = current.Level;
            if (level is { } l && l != current.Level)
            {
                nextLevel = l;
                changed.Add("level");
            }

            var nextCap = current.GroupCap;
            if (groupCap is { } c && c != current.GroupCap)
            {
                nextCap = c;
                changed.Add("groupCap");
            }

            var nextEnabled = current.HygieneEnabled;
            if (hygieneEnabled is { } he && he != current.HygieneEnabled)
            {
                nextEnabled = he;
                changed.Add("hygiene.enabled");
            }

            var nextRegenerate = current.HygieneRegenerate;
            if (hygieneRegenerate is { } hr && hr != current.HygieneRegenerate)
            {
                nextRegenerate = hr;
                changed.Add("hygiene.regenerate");
            }

            // 会话覆盖：单条增删，不做整体覆盖 —— 整体覆盖会让「只想给 A 群降档」顺手清掉 B 群
            var sessions = new Dictionary<string, ExpressLevel>(
                current.SessionOverrides, StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var id = sessionId.Trim();
                if (sessionLevel is { } target)
                {
                    if (!sessions.TryGetValue(id, out var existing) || existing != target)
                    {
                        sessions[id] = target;
                        changed.Add($"session:{id}");
                    }
                }
                else if (sessions.Remove(id))
                {
                    changed.Add($"session:{id}(cleared)");
                }
            }

            // 规则段：null = 不改，空串 = 恢复默认（与 DouyinOptionsService 的 token 同口径）
            string? nextPrompt = promptExpress;
            var promptChanged = false;
            if (promptExpress is not null)
            {
                var normalized = string.IsNullOrWhiteSpace(promptExpress) ? null : promptExpress.Trim();
                promptChanged = !string.Equals(normalized, current.CustomRules, StringComparison.Ordinal);
                nextPrompt = normalized;
                if (promptChanged)
                {
                    changed.Add("promptExpress");
                }
            }

            if (changed.Count == 0)
            {
                return changed;
            }

            var next = new ExpressSettings
            {
                Level = nextLevel,
                GroupCap = nextCap,
                SessionOverrides = sessions,
                HygieneEnabled = nextEnabled,
                HygieneRegenerate = nextRegenerate,
                CustomRules = nextPrompt,
                UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UpdatedBy = actor,
            };

            await _settings.SetAsync(StorageKey, Serialize(next), ct).ConfigureAwait(false);

            if (promptChanged)
            {
                await _settings.SetAsync(PromptKey, nextPrompt ?? string.Empty, ct).ConfigureAwait(false);
            }

            _current = next;

            _logger.Information(
                "表达档位已更新：{Changed}（现在 {Level}，群上限 {Cap}）",
                string.Join(",", changed),
                ExpressLevelText.ToLabel(next.Level),
                ExpressLevelText.ToLabel(next.GroupCap));

            return changed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 整体写回一份快照（<c>rollbackExpress</c> 专用）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="UpdateAsync"/> 分开：回滚要么**全成**、要么一动不动，
    /// 走「逐项 diff」等于把快照拆成几次写，中途失败就会停在半旧半新。
    /// </remarks>
    public async Task ApplySnapshotAsync(ExpressSettings snapshot, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var restored = snapshot with
            {
                UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                UpdatedBy = actor,
            };

            await _settings.SetAsync(StorageKey, Serialize(restored), ct).ConfigureAwait(false);
            await _settings
                .SetAsync(PromptKey, restored.CustomRules ?? string.Empty, ct)
                .ConfigureAwait(false);

            _current = restored;

            _logger.Warning(
                "表达档位已回滚：{Level}（群上限 {Cap}，会话覆盖 {Count} 个）",
                ExpressLevelText.ToLabel(restored.Level),
                ExpressLevelText.ToLabel(restored.GroupCap),
                restored.SessionOverrides.Count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string Serialize(ExpressSettings settings) => JsonSerializer.Serialize(new
    {
        level = ExpressLevelText.ToKey(settings.Level),
        groupCap = ExpressLevelText.ToKey(settings.GroupCap),
        sessions = settings.SessionOverrides.ToDictionary(
            kv => kv.Key, kv => ExpressLevelText.ToKey(kv.Value), StringComparer.Ordinal),
        hygiene = new
        {
            enabled = settings.HygieneEnabled,
            regenerate = settings.HygieneRegenerate,
        },
        updatedAtMs = settings.UpdatedAtMs,
        updatedBy = settings.UpdatedBy,
    });

    /// <summary>
    /// 解析单键；坏值逐项按默认处理并**记日志**（不静默、不抛 —— 启动不该因为一个手改坏的键失败）。
    /// </summary>
    private ExpressSettings Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ExpressSettings();
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger.Error("表达档位配置不是对象，已按默认（日常）处理：{Raw}", raw);
                return new ExpressSettings();
            }

            var level = ReadLevel(root, "level", ExpressLevel.Daily);
            var cap = ReadLevel(root, "groupCap", ExpressLevel.Explicit);

            var sessions = new Dictionary<string, ExpressLevel>(StringComparer.Ordinal);
            if (root.TryGetProperty("sessions", out var sessionsNode) &&
                sessionsNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in sessionsNode.EnumerateObject())
                {
                    if (ExpressLevelText.TryParse(entry.Value.GetString(), out var over))
                    {
                        sessions[entry.Name] = over;
                    }
                    else
                    {
                        _logger.Warning(
                            "会话覆盖的档位认不出，已忽略：{Session}={Raw}", entry.Name, entry.Value);
                    }
                }
            }

            var enabled = true;
            var regenerate = true;
            if (root.TryGetProperty("hygiene", out var hygiene) && hygiene.ValueKind == JsonValueKind.Object)
            {
                enabled = ReadBool(hygiene, "enabled", true);
                regenerate = ReadBool(hygiene, "regenerate", true);
            }

            return new ExpressSettings
            {
                Level = level,
                GroupCap = cap,
                SessionOverrides = sessions,
                HygieneEnabled = enabled,
                HygieneRegenerate = regenerate,
                UpdatedAtMs = ReadLong(root, "updatedAtMs"),
                UpdatedBy = root.TryGetProperty("updatedBy", out var by) && by.ValueKind == JsonValueKind.String
                    ? by.GetString()
                    : null,
            };
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "表达档位配置无法解析，已按默认（日常）处理：{Raw}", raw);
            return new ExpressSettings();
        }
    }

    private ExpressLevel ReadLevel(JsonElement root, string name, ExpressLevel fallback)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        if (ExpressLevelText.TryParse(node.GetString(), out var level))
        {
            return level;
        }

        _logger.Warning("档位配置 {Name} 的值认不出，已按 {Fallback} 处理：{Raw}",
            name, ExpressLevelText.ToKey(fallback), node.GetString());
        return fallback;
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var node))
        {
            return fallback;
        }

        return node.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static long ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.TryGetInt64(out var value) ? value : 0;
}
