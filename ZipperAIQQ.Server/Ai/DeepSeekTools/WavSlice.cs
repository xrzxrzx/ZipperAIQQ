namespace ZipperAIQQ.Server.Ai.DeepSeekTools;

/// <summary>切出来的一段（带**相对原始音频**的起止时间）。</summary>
public sealed record WavSlice(byte[] Bytes, long StartMs, long EndMs);
