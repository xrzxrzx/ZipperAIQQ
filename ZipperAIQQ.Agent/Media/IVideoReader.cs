namespace ZipperAIQQ.Agent.Media;

/// <summary>
/// 视频理解入口（问题记录 06 / B4）：看画面（抽帧）+ 听人声（转写）。
/// </summary>
/// <remarks>
/// <para>
/// 接口放 Agent、实现放 Server —— 与 <c>IMailReader</c> / <c>IDeepSeekMediaClient</c> 同一条依赖方向：
/// 真正干活的东西（浏览器底座的帧协议、ASR 客户端）全在 Server，Agent 不知道它们的存在。
/// </para>
/// <para>
/// <b>两个能力分开报</b>：抽帧只要底座连着就行，转写还要 ASR 可用（没有 key / 关了开关就不可用）。
/// 合成一个 <c>CanRead</c> 会出现「工具在、但一调就报"转写不可用"」——模型会反复试。
/// </para>
/// <para>
/// <b>时间点由调用方给</b>：首帧 / 等分 / 区间都是业务语义，实现只负责「按这些点把图截回来」。
/// 这样 N 与间隔策略可以单测，不必起浏览器。
/// </para>
/// </remarks>
public interface IVideoReader
{
    /// <summary>能不能看画面（底座已连 + 开关开着）。</summary>
    bool CanRead { get; }

    /// <summary>能不能转写（<see cref="CanRead"/> 且 ASR 可用）。</summary>
    bool CanTranscribe { get; }

    /// <summary>不可用的人话原因（给模型与日志看）；可用时为空串。</summary>
    string UnavailableReason { get; }

    /// <summary>
    /// 取视频简介（id / 文案 / 时长）。
    /// </summary>
    /// <param name="itemId">作品 id（纯数字）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>取不到要**抛异常**（带人话原因），不要回 <c>null</c> 冒充「视频不存在」。</remarks>
    Task<VideoBrief> LocateAsync(string itemId, CancellationToken ct = default);

    /// <summary>按给定时间点抽帧。</summary>
    /// <param name="itemId">作品 id。</param>
    /// <param name="timesMs">时间点（毫秒，按升序）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<VideoFramesOutcome> FramesAsync(
        string itemId, IReadOnlyList<long> timesMs, CancellationToken ct = default);

    /// <summary>
    /// 转写一段人声。
    /// </summary>
    /// <param name="itemId">作品 id。</param>
    /// <param name="startMs">起点（毫秒）。</param>
    /// <param name="endMs">终点（毫秒）；<c>&lt;= startMs</c> 表示「到结尾」（受配置上限约束）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<VideoTranscriptOutcome> TranscribeAsync(
        string itemId, long startMs, long endMs, CancellationToken ct = default);
}
