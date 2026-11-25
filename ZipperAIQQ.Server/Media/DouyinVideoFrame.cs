namespace ZipperAIQQ.Server.Media;

/// <summary>
/// 一张抽帧。
/// </summary>
/// <param name="Index">第几张（从 0 开始，按请求里的时间点顺序）。</param>
/// <param name="AtMs">
/// **真实**时间点（毫秒）。
/// </param>
/// <remarks>
/// <see cref="AtMs"/> 来自 `<c>&lt;video&gt;.currentTime</c>` 而不是「我要的那个点」：
/// 视频尾部 seek 会被解码器夹到关键帧附近，报「第 3 张（00:42）」时必须报**画面上那个**时间。
/// </remarks>
/// <param name="Jpeg">JPEG 字节（直接进上下文当图片，不落盘）。</param>
public sealed record DouyinVideoFrame(int Index, long AtMs, byte[] Jpeg);
