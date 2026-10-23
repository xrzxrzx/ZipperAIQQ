namespace ZipperAIQQ.Channel.Audio;

/// <summary>
/// 语音转写配置。
/// </summary>
/// <remarks>
/// 按 Q5 定稿（2026-10-23）：**只用 NapCat 自带的转文字能力**，不接本地 ASR 模型。
/// 因此这里没有模型目录 / 供应商切换 —— 只有一个总开关与失败熔断。
/// </remarks>
public sealed record SpeechOptions
{
    /// <summary>是否启用语音转写（关闭时语音保留 <c>[语音]</c> 占位）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>连续失败多少次后禁用转写（避免每条语音都白等一次超时）。</summary>
    public int MaxConsecutiveFailures { get; init; } = 3;
}
