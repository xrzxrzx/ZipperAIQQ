using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Express;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Express;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Express;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 改表达档位（B6 / 表达档位设计 §8）：**改前先快照**，再落库，再记审计。
/// </summary>
/// <remarks>
/// <para>
/// 入参只给要改的项：<c>level</c> / <c>groupCap</c> / <c>sessionId</c>+<c>sessionLevel</c> /
/// <c>promptExpress</c> / <c>hygieneEnabled</c> / <c>hygieneRegenerate</c>。
/// </para>
/// <para>
/// ⚠️ 档位值认不出**直接 400**，不静默回落：静默回落会让用户以为设置生效了，
/// 而这种「界面说改了、说话方式没变」极难自证。
/// </para>
/// <para>
/// <c>sessionLevel</c> 传空串 / <c>null</c> = 删除该会话覆盖（回到全局 + 群上限）。
/// </para>
/// </remarks>
public sealed class SetExpressLevelAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly ExpressOptionsService _express;
    private readonly IExpressSnapshotStore _snapshots;
    private readonly IConfigStore _config;
    private readonly ILogger _logger;

    public SetExpressLevelAction(
        ExpressOptionsService express,
        IExpressSnapshotStore snapshots,
        IConfigStore config,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(express);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _express = express;
        _snapshots = snapshots;
        _config = config;
        _logger = logger;
    }

    public string Name => "setExpressLevel";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest,
                "payload 需要 { level?, groupCap?, sessionId?, sessionLevel?, promptExpress?, " +
                "hygieneEnabled?, hygieneRegenerate? }（可只给其中几项）。");
        }

        var level = ReadLevel(payload, "level");
        var groupCap = ReadLevel(payload, "groupCap");
        var sessionId = ReadString(payload, "sessionId");
        var sessionLevel = ReadLevel(payload, "sessionLevel");
        var promptExpress = payload.TryGetProperty("promptExpress", out var promptNode)
            ? promptNode.ValueKind switch
            {
                JsonValueKind.String => promptNode.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                _ => throw new ApiException(ApiResponse.BadRequest, "promptExpress 必须是字符串（留空 = 用内置默认）。"),
            }
            : null;
        var hygieneEnabled = ReadBool(payload, "hygieneEnabled");
        var hygieneRegenerate = ReadBool(payload, "hygieneRegenerate");

        // 给了 sessionId = 要动这个会话的覆盖：带 sessionLevel 是设/改，不带是删除。
        // 没给 sessionId = 不动任何会话覆盖（只改全局档 / 群上限 / 规则段）。
        var touchesSession = !string.IsNullOrWhiteSpace(sessionId);

        // ① 改前快照：先存旧状态，改坏了才有得退
        var before = _express.Current;
        var payloadJson = ExpressSnapshotPayload.ToJson(before);
        var snapshotId = await _snapshots
            .AppendAsync(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Actor,
                payloadJson,
                ExpressSnapshotPayload.ChecksumOf(payloadJson),
                ct)
            .ConfigureAwait(false);

        // ② 落库（只改给了的项）
        var changed = await _express.UpdateAsync(
            level,
            groupCap,
            touchesSession ? sessionId : null,
            touchesSession ? sessionLevel : null,
            promptExpress,
            hygieneEnabled,
            hygieneRegenerate,
            Actor,
            ct).ConfigureAwait(false);

        if (changed.Count == 0)
        {
            // 什么都没变：把刚写的那份撤掉。留着它只会把真正有用的历史挤走
            // （客户端连点同一个档位，每次都会走到这里）
            await _snapshots.DeleteAsync(snapshotId, ct).ConfigureAwait(false);
            _logger.Information("表达档位未变化（skipped，已撤掉本次快照）");
        }
        else
        {
            await _config
                .AppendAuditAsync("setExpressLevel", Actor, string.Join(",", changed), ct)
                .ConfigureAwait(false);

            await _snapshots.TrimAsync(ExpressSnapshotPolicy.Keep, ct).ConfigureAwait(false);
        }

        var next = _express.Current;
        var snapshots = await _snapshots.ListRecentAsync(20, ct).ConfigureAwait(false);

        return new
        {
            // saved 是**整数**（改了 n 项）不是 bool：`saveDouyin.saved` 那次事故就是这么来的
            saved = changed.Count,
            skipped = changed.Count == 0,
            snapshotId = changed.Count == 0 ? (long?)null : snapshotId,
            changed,
            level = ExpressLevelText.ToKey(next.Level),
            levelLabel = ExpressLevelText.ToLabel(next.Level),
            groupCap = ExpressLevelText.ToKey(next.GroupCap),
            groupCapLabel = ExpressLevelText.ToLabel(next.GroupCap),

            // 出参与 getExpressStatus 对齐（设计 §8 要求「同 getExpressStatus + saved/skipped/snapshotId」）：
            // 少字段会让客户端「保存后直接应用这份 DTO」时把状态行刷成「0 次 / 全开」这种假象
            source = "global",
            sessionOverrides = next.SessionOverrides
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
                enabled = next.HygieneEnabled,
                regenerate = next.HygieneRegenerate,
                hits = _express.HygieneHits,
                regenerated = _express.HygieneRegenerated,
            },
            lastChangedAt = next.UpdatedAtMs,
            lastChangedBy = next.UpdatedBy,
            promptExpress = new
            {
                customized = next.CustomRules is not null,
                length = next.CustomRules?.Length ?? 0,
                text = next.CustomRules ?? string.Empty,
            },
            snapshots = snapshots
                .Select(s => new { id = s.Id, atMs = s.AtMs, actor = s.Actor })
                .ToList(),
        };
    }

    /// <summary>档位解析：认不出就 400（不猜、不静默回落）。</summary>
    private static ExpressLevel? ReadLevel(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var node))
        {
            return null;
        }

        if (node.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(ApiResponse.BadRequest, $"{name} 必须是字符串（daily / close / explicit）。");
        }

        var raw = node.GetString();
        if (!ExpressLevelText.TryParse(raw, out var level))
        {
            throw new ApiException(ApiResponse.BadRequest,
                $"{name} 只能是 daily / close / explicit（也接受 日常 / 亲近 / 露骨），收到：{raw}");
        }

        return level;
    }

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static bool? ReadBool(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var node))
        {
            return null;
        }

        return node.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ApiException(ApiResponse.BadRequest, $"{name} 必须是 true / false。"),
        };
    }
}
