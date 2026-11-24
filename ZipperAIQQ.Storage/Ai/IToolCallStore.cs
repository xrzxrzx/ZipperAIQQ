namespace ZipperAIQQ.Storage.Ai;

/// <summary>一次全家桶调用的用量记录（**按计费单位**，不是 token）。</summary>
/// <param name="Kind">工具类别：<c>ocr</c> / <c>tts</c> / <c>asr</c>。</param>
/// <param name="Unit">单位：<c>page</c> / <c>char</c> / <c>second</c>。</param>
/// <param name="Amount">数量。</param>
public sealed record ToolCallEntry(
    string Kind, string Unit, double Amount, string? SessionId, DateTimeOffset At);

/// <summary>按类别聚合后的用量。</summary>
public sealed record ToolUsageSummary(string Kind, string Unit, long Calls, double TotalAmount);

/// <summary>
/// 全家桶用量仓储（问题记录 06 T2.7）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>CallMetrics</c> **分表**：那张表的口径是 token / 耗时，
/// 混进「页 / 字符 / 秒」会让现有的 token 统计整体失真（问题记录 06 验收 A-9 要求分开展示）。
/// </para>
/// <para>
/// 只记元数据（类别 / 单位 / 数量 / 会话），**不记被处理的内容**。
/// </para>
/// </remarks>
public interface IToolCallStore
{
    /// <summary>追加一条用量记录。</summary>
    Task AddAsync(ToolCallEntry entry, CancellationToken ct = default);

    /// <summary>
    /// 按类别聚合（按数量降序）。
    /// </summary>
    /// <param name="windowHours">回看小时数；&lt;= 0 表示不限。</param>
    /// <param name="sessionId">只统计该会话；<c>null</c> = 全部。</param>
    Task<IReadOnlyList<ToolUsageSummary>> GetSummaryAsync(
        int windowHours = 24, string? sessionId = null, CancellationToken ct = default);
}
