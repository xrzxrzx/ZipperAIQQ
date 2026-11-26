using Serilog;
using ZipperAIQQ.Storage.Douyin;

namespace ZipperAIQQ.Server.Douyin;

/// <summary>
/// 写闸门的实现（G3）：日配额（24 小时滚动窗口）/ 最小间隔 / 同人一次，全部留痕。
/// </summary>
/// <remarks>
/// <para>
/// 为什么用「24 小时滚动窗口」而不是自然日：自然日会在午夜整点清零，于是同一个人
/// 可以在 23:59 与 00:01 各写一次 —— 那正好是平台风控最敏感的形状。
/// </para>
/// <para>
/// 三条判定的顺序是刻意的：**先配额 → 再间隔 → 再同人**。配额最能给人"别再试了"的确定答案；
/// 间隔最容易被误解成"再试一次就过了"（其实只会更糟）；同人一次是最细的一条。
/// </para>
/// </remarks>
public sealed class WriteGate : IWriteGate
{
    /// <summary>配额窗口（24 小时滚动）。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly IDouyinWriteStore _store;
    private readonly ILogger _logger;

    public WriteGate(IDouyinWriteStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WriteDecision> CheckAsync(
        WriteQuota quota, string targetKey, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        if (quota.PerDay <= 0)
        {
            return WriteDecision.Deny($"{quota.Action} 的日配额是 0（等于关闭）。");
        }

        var since = now - Window;

        var used = await _store.CountSinceAsync(quota.Action, since, ct).ConfigureAwait(false);
        if (used >= quota.PerDay)
        {
            var oldest = await _store.OldestSinceAsync(quota.Action, since, ct).ConfigureAwait(false);
            TimeSpan? retryAfter = oldest is { } at
                ? Max(TimeSpan.Zero, at + Window - now)
                : null;

            _logger.Warning(
                "写闸门拒绝：action={Action} 原因=配额用尽（{Used}/{Limit}）",
                quota.Action, used, quota.PerDay);

            return WriteDecision.Deny(
                $"已经用掉 {used}/{quota.PerDay} 次（24 小时内），这次先别写了。", retryAfter);
        }

        if (quota.MinIntervalMs > 0)
        {
            var last = await _store.LastAtAsync(quota.Action, ct).ConfigureAwait(false);
            if (last is { } at)
            {
                var elapsedMs = (now - at).TotalMilliseconds;
                if (elapsedMs < quota.MinIntervalMs)
                {
                    var wait = TimeSpan.FromMilliseconds(quota.MinIntervalMs - elapsedMs);
                    _logger.Warning(
                        "写闸门拒绝：action={Action} 原因=间隔太近（{Elapsed}ms < {Min}ms）",
                        quota.Action, (long)elapsedMs, quota.MinIntervalMs);

                    return WriteDecision.Deny(
                        $"离上一次写只过了 {Math.Max(0, Math.Round(elapsedMs / 1000))} 秒，"
                        + $"至少要隔 {quota.MinIntervalMs / 1000} 秒。",
                        wait);
                }
            }
        }

        if (quota.OncePerTarget)
        {
            var exists = await _store
                .ExistsForTargetAsync(quota.Action, targetKey, since, ct)
                .ConfigureAwait(false);

            if (exists)
            {
                _logger.Warning(
                    "写闸门拒绝：action={Action} 原因=同一目标已写过（{Target}）", quota.Action, targetKey);

                return WriteDecision.Deny($"这个人/这条评论已经回过了（{targetKey}），同一目标只回一次。");
            }
        }

        return WriteDecision.Allow();
    }

    /// <inheritdoc />
    public async Task RecordAsync(
        WriteQuota quota,
        string targetKey,
        string? itemId,
        string? commentId,
        bool ok,
        string? note,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        try
        {
            await _store.AppendAsync(
                    new DouyinWriteAction(
                        Action: quota.Action,
                        TargetKey: targetKey,
                        Ok: ok,
                        At: now,
                        ItemId: itemId,
                        CommentId: commentId,
                        Note: Shorten(note)),
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 记账失败不拖垮动作本身，但**必须响**：留痕有洞比"少回一条"严重得多
            _logger.Error(
                ex, "写闸门记账失败：action={Action} target={Target} ok={Ok}", quota.Action, targetKey, ok);
        }
    }

    /// <summary>备注压成一行、限长（模型给的文案不可信，别把日志写成小作文）。</summary>
    private static string? Shorten(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var flat = note.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
