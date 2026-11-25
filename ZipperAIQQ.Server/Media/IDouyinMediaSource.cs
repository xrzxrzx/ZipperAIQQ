namespace ZipperAIQQ.Server.Media;

/// <summary>
/// 服务端 → 浏览器底座「取视频媒体」的窄接口（B4 / T4.6）。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>DouyinChannel</c> 实现（连接、requestId 配对、超时都在它手里），
/// 消费者是 <c>VideoReadService</c> 与单元测试的假实现 —— 抽视频的实现细节不外泄给 Agent。
/// </para>
/// <para>
/// 为什么抽在 Server 而不是 Agent：Agent 不该知道「帧是浏览器里的 canvas 画出来的」，
/// 也不该知道 sidecar 的帧名（与 <c>IMailReader</c> 同一条依赖方向）。
/// </para>
/// <para>
/// 失败一律抛异常（<see cref="InvalidOperationException"/> / <see cref="TimeoutException"/> / <see cref="OperationCanceledException"/>），
/// **不返回空结果冒充成功**：转写"没有声音"和"取不到声音"必须能被上层区分。
/// </para>
/// </remarks>
public interface IDouyinMediaSource
{
    /// <summary>侧车是否连着（没连上就没法取任何媒体）。</summary>
    bool IsReady { get; }

    /// <summary>不可用的人话原因；可用时为空串。</summary>
    string NotReadyReason { get; }

    /// <summary>取视频详情（时长 / 文案 / 播放地址）。</summary>
    /// <param name="itemId">作品 id（纯数字）。</param>
    /// <param name="timeoutMs">超时（毫秒）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<DouyinVideoInfo> ResolveVideoAsync(string itemId, int timeoutMs, CancellationToken ct = default);

    /// <summary>按给定时间点抽帧（时间点由调用方按业务语义算好）。</summary>
    /// <param name="itemId">作品 id。</param>
    /// <param name="timesMs">时间点（毫秒，按顺序）。</param>
    /// <param name="maxWidth">抽帧的缩放上限（宽）。</param>
    /// <param name="maxVideoBytes">视频本体下载上限（字节）。</param>
    /// <param name="timeoutMs">超时（毫秒）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<DouyinVideoFrames> CaptureFramesAsync(
        string itemId,
        IReadOnlyList<long> timesMs,
        int maxWidth,
        int maxVideoBytes,
        int timeoutMs,
        CancellationToken ct = default);

    /// <summary>取一段音频（16 kHz 单声道 WAV）。</summary>
    /// <param name="itemId">作品 id。</param>
    /// <param name="startMs">起点（毫秒）。</param>
    /// <param name="endMs">终点（毫秒）。</param>
    /// <param name="maxVideoBytes">视频本体下载上限（字节）。</param>
    /// <param name="timeoutMs">超时（毫秒）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<DouyinAudioSlice> ExtractAudioAsync(
        string itemId,
        long startMs,
        long endMs,
        int maxVideoBytes,
        int timeoutMs,
        CancellationToken ct = default);
}
