namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 一次抽帧的「取点计划」：模式 + 张数 + 区间（毫秒）。
/// </summary>
/// <param name="Kind">取点方式。</param>
/// <param name="Count">张数（<see cref="FramePlanKind.First"/> 时无意义）。</param>
/// <param name="StartMs">区间起点（毫秒；仅 <see cref="FramePlanKind.Range"/> 用）。</param>
/// <param name="EndMs">
/// 区间终点（毫秒；仅 <see cref="FramePlanKind.Range"/> 用）。
/// <c>0</c> = 到视频结尾 —— 此时需要真实时长才能落实。
/// </param>
public sealed record FramePlan(FramePlanKind Kind, int Count, long StartMs, long EndMs);
