using Serilog;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Storage.Ai;

namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// <see cref="IToolUsageSink"/> 的落库实现（问题记录 06 T2.7）。
/// </summary>
/// <remarks>
/// <para>
/// 记账失败**只 Warning**：用量是观测，工具结果是交付 ——
/// 为了记一笔账把「图已经识别完」的结果弄丢，是本末倒置。
/// </para>
/// <para>
/// 不记内容（不存图、不存文本），只记类别 / 单位 / 数量 / 会话：这张表迟早被拿去做账单，
/// 里面不该有用户的正文。
/// </para>
/// </remarks>
public sealed class ToolUsageSink : IToolUsageSink
{
    /// <summary>
    /// 连续失败计数（进程级）。用途见 <see cref="RecordAsync"/> 的注释。
    /// </summary>
    private static long _failureCount;

    private readonly IToolCallStore _store;
    private readonly ILogger _logger;

    public ToolUsageSink(IToolCallStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _logger = logger;
    }

    /// <summary>累计失败次数（给状态页判断「统计是否可信」用；进程重启归零）。</summary>
    public static long FailureCount => Interlocked.Read(ref _failureCount);

    public async Task RecordAsync(
        string kind, string unit, double amount, string? sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(unit))
        {
            return;
        }

        try
        {
            await _store
                .AddAsync(new ToolCallEntry(kind, unit, amount, sessionId, DateTimeOffset.UtcNow), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 第一次失败必须是 Error：表不存在 / 迁移没跑 / 库只读 这类**系统性**故障，
            // 若只打 Warning，用量会静默归零，而数据页显示「暂无调用」——用户以为没用过。
            // 之后每 100 次打一条，避免刷屏。
            var failures = Interlocked.Increment(ref _failureCount);
            if (failures == 1 || failures % 100 == 0)
            {
                _logger.Error(
                    ex,
                    "工具用量记账失败（累计 {Failures} 次）：统计可能归零，请检查 ToolCalls 表与库权限",
                    failures);
            }
        }
    }
}
