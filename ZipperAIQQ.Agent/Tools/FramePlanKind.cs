namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 抽帧的三种取点方式（对应 <c>douyin_video</c> 的 <c>mode</c> 参数）。
/// </summary>
/// <remarks>
/// 单独成类型而不是在工具里 if/else：取点规则是**纯逻辑**，要能被单测锁住
/// （张数上限、区间端点、时长未知时的降级），混在 <c>AIFunction</c> 里就测不了。
/// </remarks>
public enum FramePlanKind
{
    /// <summary>只截首帧（<c>0</c>）。</summary>
    First,

    /// <summary>整条视频等分 <see cref="FramePlan.Count"/> 张。</summary>
    Even,

    /// <summary>在 <see cref="FramePlan.StartMs"/>–<see cref="FramePlan.EndMs"/> 之间等分。</summary>
    Range,
}
