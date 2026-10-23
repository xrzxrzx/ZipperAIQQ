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

    /// <summary>
    /// 单条语音最多尝试几次（含首次）。
    /// </summary>
    /// <remarks>
    /// 实测：语音刚到达时立刻转写，QQ 侧 ASR 可能还没出结果（NapCat 回 <c>1200 获取语音转文字结果失败</c>），
    /// 隔一会儿再问就成功。所以默认重试一次，而不是直接判失败。
    /// </remarks>
    public int MaxAttempts { get; init; } = 2;

    /// <summary>重试前等待多久（毫秒）。</summary>
    public int RetryDelayMs { get; init; } = 700;
}
