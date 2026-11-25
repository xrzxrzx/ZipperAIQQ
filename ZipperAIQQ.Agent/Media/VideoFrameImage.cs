namespace ZipperAIQQ.Agent.Media;

/// <summary>
/// 一张抽帧（进上下文当视觉输入，不落盘）。
/// </summary>
/// <param name="Index">第几张（从 0 开始）。</param>
/// <param name="AtMs">
/// **真实**时间点（毫秒）。报「第 3 张（00:42）」时用它，不要用「我要的那个点」：
/// 视频尾部 seek 会被解码器夹到关键帧附近，两者可能差几百毫秒。
/// </param>
/// <param name="Bytes">JPEG 字节。</param>
/// <param name="Mime">MIME 类型（当前固定 <c>image/jpeg</c>）。</param>
public sealed record VideoFrameImage(int Index, long AtMs, byte[] Bytes, string Mime);
