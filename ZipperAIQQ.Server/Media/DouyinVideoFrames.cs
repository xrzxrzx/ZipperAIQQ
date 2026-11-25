namespace ZipperAIQQ.Server.Media;

/// <summary>
/// 一批抽帧的结果。
/// </summary>
/// <param name="DurationMs">视频总时长（毫秒，来自 `<c>&lt;video&gt;.duration</c>`）。</param>
/// <param name="Width">画面宽度（**已按配置缩过**，不是原始分辨率）。</param>
/// <param name="Height">画面高度。</param>
/// <param name="Frames">按请求顺序的帧。</param>
public sealed record DouyinVideoFrames(
    long DurationMs,
    int Width,
    int Height,
    IReadOnlyList<DouyinVideoFrame> Frames);
