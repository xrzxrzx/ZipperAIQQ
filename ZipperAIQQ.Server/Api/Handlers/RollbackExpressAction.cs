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
/// 回滚表达档位到某份快照（B6 / 表达档位设计 §8）。
/// </summary>
/// <remarks>
/// <para>
/// 回滚**先验校验和再写**：不一致说明库里那份被手改过，
/// 把来路不明的配置当真相写回去，比明确拒绝更难收拾（A-24）。
/// </para>
/// <para>
/// 回滚本身也写一份快照（回滚前的状态）：回滚错了还能再回滚回来。
/// 这一条是「回滚按钮」和「单向破坏」的分界线。
/// </para>
/// </remarks>
public sealed class RollbackExpressAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly ExpressOptionsService _express;
    private readonly IExpressSnapshotStore _snapshots;
    private readonly IConfigStore _config;
    private readonly ILogger _logger;

    public RollbackExpressAction(
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

    public string Name => "rollbackExpress";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("id", out var idNode) ||
            !idNode.TryGetInt64(out var id))
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 { id }（快照主键，来自 getExpressStatus.snapshots）。");
        }

        var snapshot = await _snapshots.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new ApiException(ApiResponse.BadRequest, $"快照不存在：{id}");

        var actual = ExpressSnapshotPayload.ChecksumOf(snapshot.PayloadJson);
        if (!string.Equals(actual, snapshot.Checksum, StringComparison.Ordinal))
        {
            throw new ApiException(ApiResponse.BadRequest,
                $"快照 {id} 校验和不一致（库里那份可能被手改过），拒绝回滚。");
        }

        if (!ExpressSnapshotPayload.TryParse(snapshot.PayloadJson, out var target, out var error))
        {
            throw new ApiException(ApiResponse.BadRequest, $"快照 {id} 内容无法解析，拒绝回滚：{error}");
        }

        // 回滚前也留一份：回滚错了还能再退回来
        var before = ExpressSnapshotPayload.ToJson(_express.Current);
        await _snapshots
            .AppendAsync(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Actor,
                before,
                ExpressSnapshotPayload.ChecksumOf(before),
                ct)
            .ConfigureAwait(false);

        await _express.ApplySnapshotAsync(target, Actor, ct).ConfigureAwait(false);

        await _config
            .AppendAuditAsync("rollbackExpress", Actor, $"snapshot={id}", ct)
            .ConfigureAwait(false);

        var next = _express.Current;
        return new
        {
            restored = true,
            snapshotId = id,
            level = ExpressLevelText.ToKey(next.Level),
            levelLabel = ExpressLevelText.ToLabel(next.Level),
            groupCap = ExpressLevelText.ToKey(next.GroupCap),
            groupCapLabel = ExpressLevelText.ToLabel(next.GroupCap),
            promptExpress = new
            {
                customized = next.CustomRules is not null,
                length = next.CustomRules?.Length ?? 0,
            },
        };
    }
}
