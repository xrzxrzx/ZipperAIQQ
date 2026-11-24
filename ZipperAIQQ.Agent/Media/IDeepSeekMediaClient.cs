namespace ZipperAIQQ.Agent.Media;

/// <summary>
/// OCR 的识别目的（对应 <c>POST /v1/tools/ocr</c> 的 <c>purpose</c> 字段）。
/// </summary>
public enum OcrPurpose
{
    /// <summary>纯文本提取（<c>text_recognition</c>）：只要字，不要版面。</summary>
    TextRecognition,

    /// <summary>逐页版面识别（<c>rich_layout_recognition</c>）：保留阅读顺序与结构。</summary>
    RichLayoutRecognition,
}

/// <summary>一页 OCR 结果（<paramref name="Index"/> 从 1 开始）。</summary>
public sealed record OcrPage(int Index, string Text);

/// <summary>
/// OCR 结果。
/// </summary>
/// <param name="PageCount">平台回报的页数（**不一定等于** <see cref="Pages"/> 的条数，缺页时以它为准提示）。</param>
/// <param name="InputTokens">平台计费的输入 token（拿不到就是 <c>null</c>，不要当 0）。</param>
/// <param name="OutputTokens">平台计费的输出 token。</param>
public sealed record OcrResult(
    int PageCount,
    IReadOnlyList<OcrPage> Pages,
    long? InputTokens = null,
    long? OutputTokens = null);

/// <summary>
/// TTS 产物（**只落文件**，问题记录 06 Q-07 拍板）。
/// </summary>
/// <param name="FilePath">服务器上的绝对路径。</param>
/// <param name="Characters">
/// 计费字符数。**按输入文本本地估算**（UTF-16 码元数，表情算 2），平台不回这个数时以它为准。
/// </param>
public sealed record TtsResult(string FilePath, long Bytes, string Format, int Characters);

/// <summary>一段 ASR 转写（<paramref name="StartMs"/>/<paramref name="EndMs"/> 是**相对原始音频**的时间）。</summary>
public sealed record AsrSegment(long StartMs, long EndMs, string Text);

/// <summary>
/// ASR 结果。
/// </summary>
/// <param name="Text">全部段拼起来的文本（段间以换行分隔）。</param>
/// <param name="DurationMs">识别到的音频总时长（**原始音频**的时长，不是分段累加）。</param>
/// <param name="Parts">
/// 逐段时间轴。长音频切片后每段都带自己的起止毫秒，
/// B4 的字幕/摘要要用它，所以在这里就保留下来（别算完就扔）。
/// </param>
public sealed record AsrResult(string Text, long DurationMs, IReadOnlyList<AsrSegment> Parts);

/// <summary>
/// DeepSeek 全家桶里「媒体类」三个独立接口（OCR / TTS / ASR）。
/// </summary>
/// <remarks>
/// <para>
/// 接口放 Agent、实现放 Server —— 与 <c>IWebSearchProvider</c> 同一条依赖方向：
/// Agent 拿不到明文 key，由 Server 解出后注入。
/// </para>
/// <para>
/// 三个 <c>*Available</c> 是**给工具可见性用的**：不可用时连 schema 都不给模型，
/// 免得它反复调一个注定失败的工具（与 <c>web_search</c> 同口径）。
/// </para>
/// <para>
/// 失败统一抛 <see cref="DeepSeekMediaException"/>，带可读的分类与状态码；
/// **不要**在这里把失败吞成空结果 —— 模型需要知道「没做到」，才不会编造。
/// </para>
/// </remarks>
public interface IDeepSeekMediaClient
{
    bool OcrAvailable { get; }

    bool TtsAvailable { get; }

    /// <summary>ASR 是否可用（B4 的视频转写照它决定要不要给 <c>douyin_transcript</c>）。</summary>
    bool AsrAvailable { get; }

    /// <summary>识别图片文字。</summary>
    /// <param name="file">图片字节（png / jpg / webp / pdf 等平台支持的格式）。</param>
    /// <param name="fileName">传给平台的文件名（影响平台对格式的判断，带上扩展名）。</param>
    Task<OcrResult> OcrAsync(
        byte[] file, string fileName, OcrPurpose purpose, CancellationToken ct = default);

    /// <summary>
    /// 文本转语音，产物落到服务器本地文件（不发送到会话）。
    /// </summary>
    /// <param name="voiceId">音色；<c>null</c> 用配置里的默认值。</param>
    Task<TtsResult> TtsAsync(string text, string? voiceId = null, CancellationToken ct = default);

    /// <summary>
    /// 语音转文字。**长音频由实现自己切片**（平台单次 ≤120 s / ≤8 MiB），调用方不用管。
    /// </summary>
    Task<AsrResult> AsrAsync(byte[] audio, string fileName, CancellationToken ct = default);
}
