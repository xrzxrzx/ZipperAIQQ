namespace ZipperAIQQ.Server.Ai.DeepSeekTools;

/// <summary>WAV 的格式信息（只认线性 PCM —— 别的编码交给平台自己判）。</summary>
/// <param name="AudioFormat">1 = PCM。</param>
/// <param name="BlockAlign">每帧字节数（channels × bytesPerSample）。</param>
public sealed record WavInfo(
    int AudioFormat,
    int Channels,
    int SampleRate,
    int BlockAlign,
    int BitsPerSample,
    int DataOffset,
    int DataLength)
{
    /// <summary>每秒字节数。</summary>
    public int BytesPerSecond => SampleRate * BlockAlign;

    /// <summary>总时长（毫秒）。</summary>
    public long DurationMs =>
        BytesPerSecond <= 0 ? 0 : (long)DataLength * 1000 / BytesPerSecond;
}
