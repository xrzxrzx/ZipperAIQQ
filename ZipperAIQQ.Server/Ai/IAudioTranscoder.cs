namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// 把任意音频字节转成 **16 kHz 单声道 PCM WAV**（ASR 接口要的形态）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：ASR 只收 16 位 PCM WAV 或 Ogg Opus
/// （[官方指南](https://api-docs.deepseek.com/zh-cn/guides/asr/)，按文件内容判断），
/// 而 QQ 的 <c>record.data.url</c> 下载下来是 **AMR**、本地原件是 **SILK**。
/// </para>
/// <para>
/// 抽接口而不是直接写死进程调用：单测不该为了跑一条用例去装 82 MB 的 ffmpeg
/// （与 <c>IMailRenderer</c> 同一条理由）。
/// </para>
/// </remarks>
public interface IAudioTranscoder
{
    /// <summary>转码；成功返回 WAV 字节，失败（没装 / 超时 / 格式不支持）返回 <c>null</c>。</summary>
    /// <param name="source">原始音频字节。</param>
    /// <param name="format">格式提示（<c>amr</c> / <c>silk</c> / …）；<c>null</c> = 让 ffmpeg 自己判断。</param>
    /// <param name="ct">取消令牌。</param>
    Task<byte[]?> ToWavAsync(byte[] source, string? format, CancellationToken ct = default);
}
