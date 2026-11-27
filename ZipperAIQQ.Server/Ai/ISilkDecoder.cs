namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// 把 QQ 的 SILK 语音解码成 16 kHz 单声道 PCM WAV（B00001）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：QQ 语音文件名叫 <c>.amr</c>，头部其实是 <c>#!SILK_V3</c>；
/// 机器上的 <c>ffmpeg.exe</c> 是坏的（<c>not a valid application for this OS platform</c>），
/// 而 NapCat 的 <c>get_record</c> 要等 QQ 的消息索引（语音刚到时查不到）。
/// </para>
/// <para>
/// 抽接口是为了可测：单测不该为了跑一条用例去装 Node 或 silk-wasm
/// （与 <c>IMailRenderer</c> 同一条理由）。
/// </para>
/// </remarks>
public interface ISilkDecoder
{
    /// <summary>
    /// 解码成 WAV。
    /// </summary>
    /// <param name="silk">silk 字节（含 <c>#!SILK_V3</c> 头）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>WAV 字节；失败（Node 不在 / 不是 silk / 超时）返回 <c>null</c>。</returns>
    Task<byte[]?> ToWavAsync(byte[] silk, CancellationToken ct = default);
}
