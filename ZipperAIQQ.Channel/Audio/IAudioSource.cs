namespace ZipperAIQQ.Channel.Audio;

/// <summary>
/// 把平台上的语音**取成 PCM WAV 字节**（B00001）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是"取 WAV"而不是"取原始字节 + 自己转码"：QQ 的语音是 <c>SILK_V3</c>
/// （文件名叫 <c>.amr</c> 也一样，2026-11-28 实测头部是 <c>#!SILK_V3</c>），
/// 而本机上的 ffmpeg **是坏的**（<c>not a valid application for this OS platform</c>）。
/// 但 NapCat 自己能解：<c>get_record</c> 带 <c>out_format: "wav"</c> 会返回
/// 24 kHz 单声道 16-bit 的 RIFF/WAVE（实测 361 ms）。
/// </para>
/// <para>
/// 所以转码这活交给**本来就懂这个格式的那一端**，我们只管取字节。
/// </para>
/// </remarks>
public interface IAudioSource
{
    /// <summary>
    /// 取语音的 WAV 字节。
    /// </summary>
    /// <param name="fileId">平台文件标识（OneBot 的 <c>record.data.file</c>）。</param>
    /// <param name="filePath">平台给的本地原始路径（可选，仅用于诊断）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>WAV 字节；取不到返回 <c>null</c>（契约同 <see cref="ISpeechTranscriber"/>：不抛）。</returns>
    Task<byte[]?> GetWavAsync(string? fileId, string? filePath, CancellationToken ct = default);
}
