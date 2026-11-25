namespace ZipperAIQQ.Server.Media;

/// <summary>
/// 一段音频窗口：16 kHz 单声道 PCM16 WAV，时间轴**相对视频开头**。
/// </summary>
/// <param name="Wav">完整 WAV 字节（含 44 字节 RIFF 头，可直接喂 <c>WavSlicer</c> / ASR）。</param>
/// <param name="SampleRate">采样率（当前固定 16000）。</param>
/// <param name="StartMs">这段音频在视频里的起点（毫秒）。</param>
/// <param name="EndMs">终点（毫秒，不含）。</param>
/// <param name="DurationMs">
/// **整条视频**的时长（毫秒），不是这一段。
/// </param>
/// <remarks>
/// 为什么把「这段的区间」和「整条多长」分开放：服务端要按 <see cref="DurationMs"/> 算还差几个窗口，
/// 而拼回整段时要用每段自己的 <see cref="StartMs"/> 对齐 —— 两个数字混在一起迟早算错。
/// </remarks>
public sealed record DouyinAudioSlice(
    byte[] Wav,
    int SampleRate,
    long StartMs,
    long EndMs,
    long DurationMs);
