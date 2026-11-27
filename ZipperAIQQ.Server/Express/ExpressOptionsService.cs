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
/// 规则段（<c>prompt.express</c>）现在是 <c>express</c> JSON 里的 <c>promptExpress</c> 字段。
/// 老键只在启动时读一次并迁移过来（**不删**，留人工恢复）：两份真相必然漂移，
/// 而漂移的表现是「回滚后规则段还是新的」这种最难查的一类。
/// </para>
/// </remarks>
public sealed class ExpressOptionsService : IExpressSettingsSource
{
    /// <summary>档位单键（JSON）。</summary>
    public const string StorageKey = "express";

    /// <summary>
    /// 老键（2026-11-28 之前的独立规则段键）：**只用于一次性迁移**，写路径不再碰它。
    /// </summary>
    public const string LegacyPromptKey = "prompt.express";

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

        var usable = ExpressSnapshotPayload.TryParse(raw, out var parsed, out var error);

        if (!usable)
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                // 键存在但内容解析不了：报 Error（那不是"首次启动"，是配置坏了）
                _logger.Error("表达档位配置无法解析，已按默认（日常）处理：{Reason}", error);
            }

            parsed = new ExpressSettings();
        }
        else if (error is not null)
        {
            _logger.Warning("表达档位配置有项认不出：{Reason}", error);
        }

        // 老键迁移：**只在没有可用的 `express` 配置时**做一次（键不存在，或内容解析不了）。
        //
        // ⚠️ 不能写成「JSON 里 promptExpress 是空就迁移」：老键不删（留人工恢复），
        // 那样用户点了「恢复默认规则段」之后，下次重启又被搬回来 ——
        // 而且每次重启都重写一次，表现成「恢复默认没生效」，极难自证。
        if (!usable)
        {
            var legacyPrompt = await _settings.GetAsync(LegacyPromptKey, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(legacyPrompt))
            {
                parsed = parsed with { CustomRules = legacyPrompt.Trim() };
                await _settings
                    .SetAsync(StorageKey, ExpressSnapshotPayload.ToJson(parsed), ct)
                    .ConfigureAwait(false);

                _logger.Information(
                    "已把老的 {Legacy} 迁移进 {Key}（老键保留不删，之后不再参与）",
                    LegacyPromptKey, StorageKey);
            }
        }

        _current = parsed;

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
    /// <param name="sessionLevel">该会话的档位；传 <c>null</c> = **删除**这条覆盖（回到全局/上限）。</param>
    /// <param name="promptExpress">规则段覆盖文本；<c>null</c> = 不改，空串 = 恢复内置默认。</param>
    /// <param name="hygieneEnabled">免拒绝校准总开关；<c>null</c> = 不改。</param>
    /// <param name="hygieneRegenerate">命中后是否重生成；<c>null</c> = 不改。</param>
    /// <param name="actor">操作者（写进 <c>updatedBy</c>）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际改动的字段名（供审计与回显）；空 = 没有任何变化。</returns>
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
            var nextPrompt = current.CustomRules;
            if (promptExpress is not null)
            {
                var normalized = string.IsNullOrWhiteSpace(promptExpress) ? null : promptExpress.Trim();
                if (!string.Equals(normalized, current.CustomRules, StringComparison.Ordinal))
                {
                    nextPrompt = normalized;
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

            await _settings
                .SetAsync(StorageKey, ExpressSnapshotPayload.ToJson(next), ct)
                .ConfigureAwait(false);

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

            await _settings
                .SetAsync(StorageKey, ExpressSnapshotPayload.ToJson(restored), ct)
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
}
