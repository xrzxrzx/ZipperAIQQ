namespace ZipperAIQQ.Server.Billing;

/// <summary>一次「当前窗口花了多少」的读取结果。</summary>
/// <param name="SpentCny">窗口花费（元）。</param>
/// <param name="Source">口径来源：balance / estimate / none。</param>
/// <param name="RemainingCny">剩余余额（可空）。</param>
/// <param name="HasData">是否有可信数据；<c>false</c> 时闸门**不应**拦人。</param>
public sealed record SpendSnapshot(decimal SpentCny, string Source, decimal? RemainingCny, bool HasData);
