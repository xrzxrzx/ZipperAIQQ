namespace ZipperAIQQ.Agent.Express;

/// <summary>
/// 一次免拒绝校准扫描的结果。
/// </summary>
/// <param name="Category">命中的类别；<c>null</c> = 没命中。</param>
/// <param name="Preview">命中片段（已压换行、截断），只用于日志与状态展示。</param>
/// <remarks>
/// 不把整段回复放进日志：回复是**用户内容**，日志里留一句话的片段足够定位，
/// 留全文只会让日志变成第二份聊天记录。
/// </remarks>
public sealed record ExpressHygieneScan(ExpressHygieneCategory? Category, string? Preview)
{
    /// <summary>没命中。</summary>
    public static ExpressHygieneScan None { get; } = new(null, null);

    /// <summary>是否命中。</summary>
    public bool Hit => Category is not null;
}
