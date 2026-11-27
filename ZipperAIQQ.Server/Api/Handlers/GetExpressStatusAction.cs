using System.Text.Json;
using ZipperAIQQ.Agent.Express;
using ZipperAIQQ.Server.Express;
using ZipperAIQQ.Storage.Express;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 读表达档位状态（B6 / 表达档位设计 §8）。
/// </summary>
/// <remarks>
/// <para>
/// 一次给全：当前档、来源、会话覆盖、卫生命中、上次改动、规则段是否自定义、可用回滚点。
/// 状态分散在几个 action 里的话，客户端要串三次请求才能拼出一行状态行。
/// </para>
/// <para>
/// 可选 <c>sessionId</c>：带上它 <c>source</c> 才会区分「全局」与「该会话被单独设置」；
/// 不带就只有全局视角（客户端的状态行正是这么用的）。
/// </para>
/// </remarks>
public sealed class GetExpressStatusAction : IApiAction
{
    private readonly ExpressOptionsService _express;
    private readonly IExpressSnapshotStore _snapshots;

    public GetExpressStatusAction(ExpressOptionsService express, IExpressSnapshotStore snapshots)
    {
        ArgumentNullException.ThrowIfNull(express);
        ArgumentNullException.ThrowIfNull(snapshots);

        _express = express;
        _snapshots = snapshots;
    }

    public string Name => "getExpressStatus";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var current = _express.Current;

        var sessionId = payload.ValueKind == JsonValueKind.Object &&
                        payload.TryGetProperty("sessionId", out var node) &&
                        node.ValueKind == JsonValueKind.String
            ? node.GetString()?.Trim()
            : null;

        var hasOverride = !string.IsNullOrEmpty(sessionId) &&
                          current.SessionOverrides.ContainsKey(sessionId);

        var snapshots = await _snapshots.ListRecentAsync(20, ct).ConfigureAwait(false);

        return new
        {
            level = ExpressLevelText.ToKey(current.Level),
            levelLabel = ExpressLevelText.ToLabel(current.Level),
            groupCap = ExpressLevelText.ToKey(current.GroupCap),
            groupCapLabel = ExpressLevelText.ToLabel(current.GroupCap),
            source = hasOverride ? "session" : "global",
            sessionOverrides = current.SessionOverrides
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new
                {
                    sessionId = kv.Key,
                    level = ExpressLevelText.ToKey(kv.Value),
                    levelLabel = ExpressLevelText.ToLabel(kv.Value),
                })
                .ToList(),
            hygiene = new
            {
                enabled = current.HygieneEnabled,
                regenerate = current.HygieneRegenerate,
                hits = _express.HygieneHits,
                regenerated = _express.HygieneRegenerated,
            },
            lastChangedAt = current.UpdatedAtMs,
            lastChangedBy = current.UpdatedBy,
            promptExpress = new
            {
                customized = current.CustomRules is not null,
                length = current.CustomRules?.Length ?? 0,
            },
            snapshots = snapshots
                .Select(s => new { id = s.Id, atMs = s.AtMs, actor = s.Actor })
                .ToList(),
        };
    }
}
