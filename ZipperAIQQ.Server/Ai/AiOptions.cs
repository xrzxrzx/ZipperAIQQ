namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// 全家桶「媒体三件套」（OCR / TTS / ASR）的运行参数（<c>ai</c> 配置节）。
/// </summary>
/// <remarks>
/// <para>
/// 放在 Server/Ai 而不是 Core：**Options 类型跟着消费者走**（配置与日志设计 §5.1）。
/// 消费者是这里三个客户端 + 组合根。
/// </para>
/// <para>
/// 三个 <c>*Enabled</c> 默认都开：**能力开关**与「有没有 key」是两件事 ——
/// 没 key 时由 <see cref="IDeepSeekKeyProvider"/> 那边降级，不该让人再关一次。
/// </para>
/// </remarks>
public sealed record AiOptions
{
    /// <summary>接口基地址（三个接口同源，默认 DeepSeek 官方）。</summary>
    public string BaseUrl { get; init; } = "https://api.deepseek.com";

    /// <summary>OCR 开关（工具 <c>ocr_image</c> 是否对模型可见）。</summary>
    public bool OcrEnabled { get; init; } = true;

    /// <summary>TTS 开关（工具 <c>tts_speak</c> 是否对模型可见）。</summary>
    public bool TtsEnabled { get; init; } = true;

    /// <summary>ASR 开关（B4 的视频转写要它；本期只做客户端）。</summary>
    public bool AsrEnabled { get; init; } = true;

    /// <summary>默认音色：<c>stella</c>（女）/ <c>tide</c>（男）。</summary>
    public string TtsVoiceId { get; init; } = "stella";

    /// <summary>默认音频格式（<c>wav</c> / <c>pcm</c>）。</summary>
    public string TtsFormat { get; init; } = "wav";

    /// <summary>TTS 产物目录（**只落文件**，不发送到会话；见问题记录 06 Q-07）。</summary>
    public string TtsOutputDir { get; init; } = "./data/ai-tts";

    /// <summary>单次 HTTP 调用超时（秒）。</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// 上传体积上限（字节）。
    /// </summary>
    /// <remarks>
    /// 平台自己的限制更宽（OCR ≤100 MiB），这里卡在 20 MiB 是**故意更严**：
    /// 把整个 PDF / 长音频一次塞进来既费钱又费内存，先卡住，需要时再调配置。
    /// </remarks>
    public int MaxUploadBytes { get; init; } = 20 * 1024 * 1024;
}
