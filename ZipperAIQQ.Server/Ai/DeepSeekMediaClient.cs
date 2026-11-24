using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Server.Ai.DeepSeekTools;

namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// <see cref="IDeepSeekMediaClient"/> 的组合实现：把三个独立接口拼成一个对 Agent 暴露的门面。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要有门面：Agent 只想要「能 OCR / 能朗读 / 能转写」这三件事，
/// 不该知道它们背后是三个不同的 endpoint、三份不同的 multipart 细节。
/// </para>
/// <para>
/// 可用性 = **配置开着** 且 **有一把能用的 key**。缺任一条就返回 <c>false</c>，
/// 工具层据此**连 schema 都不给模型**（与 <c>web_search</c> 同口径）。
/// </para>
/// </remarks>
public sealed class DeepSeekMediaClient : IDeepSeekMediaClient
{
    private readonly OcrClient _ocr;
    private readonly TtsClient _tts;
    private readonly AsrClient _asr;
    private readonly AiOptions _options;
    private readonly IDeepSeekKeyProvider _keys;

    public DeepSeekMediaClient(
        OcrClient ocr,
        TtsClient tts,
        AsrClient asr,
        AiOptions options,
        IDeepSeekKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(tts);
        ArgumentNullException.ThrowIfNull(asr);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keys);

        _ocr = ocr;
        _tts = tts;
        _asr = asr;
        _options = options;
        _keys = keys;
    }

    public bool OcrAvailable => _options.OcrEnabled && _keys.HasKey;

    public bool TtsAvailable => _options.TtsEnabled && _keys.HasKey;

    public bool AsrAvailable => _options.AsrEnabled && _keys.HasKey;

    public Task<OcrResult> OcrAsync(
        byte[] file, string fileName, OcrPurpose purpose, CancellationToken ct = default) =>
        _ocr.OcrAsync(file, fileName, purpose, ct);

    public Task<TtsResult> TtsAsync(string text, string? voiceId, CancellationToken ct = default) =>
        _tts.TtsAsync(text, voiceId, ct);

    public Task<AsrResult> AsrAsync(byte[] audio, string fileName, CancellationToken ct = default) =>
        _asr.AsrAsync(audio, fileName, ct);
}
