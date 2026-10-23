namespace ZipperAIQQ.Channel.Audio;

/// <summary>
/// 一段待转写的语音。
/// </summary>
/// <param name="FileId">平台文件标识（如 <c>abc.amr</c>）。</param>
/// <param name="Url">音频地址（平台可能不给）。</param>
/// <param name="Format">格式（如 <c>amr</c> / <c>silk</c>），未知为 <c>null</c>。</param>
/// <param name="MessageId">
/// 所属消息 ID。NapCat 的 <c>fetch_ptt_text</c> **必填**这一项（实测：只给 file 会报 schema 错）。
/// </param>
public sealed record AudioClip(
    string? FileId,
    string? Url,
    string? Format = null,
    string? MessageId = null);

/// <summary>
/// 语音转文字（FR-1.5：**必须发生在通道层**，上层只读结果）。
/// </summary>
/// <remarks>
/// <para>
/// 接口放在 Channel 而不是 Agent：转写属于平台能力，
/// 上层拿到的是 <c>RecordSegment.Text</c>，**不出现任何语音分支**（FR-1.5 验收口径）。
/// </para>
/// <para>
/// 失败一律返回 <c>null</c>、**不抛异常**：一条语音转不出来，
/// 不该让整条消息丢失或让接收循环报错。
/// </para>
/// </remarks>
public interface ISpeechTranscriber
{
    /// <summary>当前是否还能尝试转写（连续失败后会被禁用）。</summary>
    bool IsAvailable { get; }

    /// <summary>转写；失败 / 不支持返回 <c>null</c>。</summary>
    Task<string?> TranscribeAsync(AudioClip clip, CancellationToken ct = default);
}
