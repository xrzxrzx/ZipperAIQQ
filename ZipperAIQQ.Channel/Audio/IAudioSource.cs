namespace ZipperAIQQ.Channel.Audio;

/// <summary>
/// 一段语音的原始字节 + 格式提示（B00001）。
/// </summary>
/// <param name="Bytes">音频字节。</param>
/// <param name="Format">格式提示（<c>silk</c> / <c>wav</c> / <c>amr</c> …），未知为 <c>null</c>。</param>
/// <remarks>
/// <see cref="Format"/> 只是提示：QQ 的语音文件名叫 <c>.amr</c>，真身是 <c>#!SILK_V3</c>。
/// 真正可靠的判据是**头部魔数**，由消费方自己看（<c>RIFF/WAVE</c> 才是 WAV）。
/// </remarks>
public sealed record AudioBytes(byte[] Bytes, string? Format);

/// <summary>
/// 把平台上的语音取成**原始音频字节**（B00001）。
/// </summary>
/// <remarks>
/// <para>
/// 2026-11-28 实测后改成"取原始字节"而不是"取 WAV"：
/// NapCat 的 <c>get_record + out_format=wav</c> 确实能把 SILK 转成 WAV（30 ms），
/// 但它要按文件名去 **QQ 的消息索引**里搜 —— 而语音**刚到达的 1~2 分钟内索引还没建好**，
/// 请求会一路等到超时（实测服务端 5 秒超时，两分钟后同一请求 30 ms 成功）。
/// </para>
/// <para>
/// 唯一"消息一到达就可用"的原料是 OneBot 给的本地路径（<c>record.data.path</c>，silk 文件）。
/// 所以这里**本地路径优先**，解码交给上层（Server 有 Node + silk-wasm），
/// <c>get_record</c> 只作兜底（老消息 / 跨机部署时路径读不到）。
/// </para>
/// </remarks>
public interface IAudioSource
{
    /// <summary>取语音的原始音频字节；取不到返回 <c>null</c>（不抛）。</summary>
    /// <param name="fileId">平台文件标识（OneBot 的 <c>record.data.file</c>）。</param>
    /// <param name="filePath">平台给的本地原始路径（OneBot 的 <c>record.data.path</c>）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<AudioBytes?> GetAudioAsync(string? fileId, string? filePath, CancellationToken ct = default);
}
