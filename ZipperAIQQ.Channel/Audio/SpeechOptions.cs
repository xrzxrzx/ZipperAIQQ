namespace ZipperAIQQ.Channel.Audio;

/// <summary>
/// 语音转写配置。
/// </summary>
/// <remarks>
/// 按 Q5 定稿（2026-10-23）：**只用 NapCat 自带的转文字能力**，不接本地 ASR 模型。
/// 因此这里没有模型目录 / 供应商切换 —— 只有总开关、单次超时与失败熔断。
/// </remarks>
public sealed record SpeechOptions
{
    /// <summary>是否启用语音转写（关闭时语音保留 <c>[语音]</c> 占位）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>连续失败多少次后熔断（避免每条语音都白等一次超时）。</summary>
    /// <remarks>
    /// 默认 2 而不是 3：一次失败已经花掉一个 <see cref="TimeoutMs"/>（B00001 实测 NapCat 侧
    /// 那条链路会卡满 30 秒），三次等于让三条语音各等一次 —— 而「等不到」这件事
    /// 第一次就该有结论。
    /// </remarks>
    public int MaxConsecutiveFailures { get; init; } = 2;

    /// <summary>
    /// 单次转写的墙上时间上限（毫秒）。**与全局 API 超时分开**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// B00001（2026-11-26）：NapCat 的 <c>fetch_ptt_text</c> 内部是
    /// 「取消息 10s → 触发 QQ 转文字 10s → 再取回 10s」三步，任一步卡住就等满一个
    /// <c>baseTimeout</c>。在语音转不动的那台机器上，三步全撞满 = 30 秒，
    /// 正好撞上全局 <c>ApiTimeoutSeconds</c>，于是**每条语音把事件接收卡 30~60 秒**。
    /// </para>
    /// <para>
    /// 所以这里不跟着全局超时走：成功路径是秒级的，失败路径不该让整条消息队列陪着等。
    /// 8 秒够成功路径用，也够我们判断"这条转不出来"。
    /// </para>
    /// </remarks>
    public int TimeoutMs { get; init; } = 8_000;

    /// <summary>
    /// 熔断后的冷却时间（毫秒）：到期后**允许再试一次**（半开）。
    /// </summary>
    /// <remarks>
    /// 没有它的话，一次熔断就要等到进程重启才恢复 —— 用户看到的会是
    /// 「语音识别永远坏着，重启一次又能试两条」，而真正的原因可能只是当次卡顿。
    /// </remarks>
    public int CooldownMs { get; init; } = 30 * 60 * 1000;

    /// <summary>
    /// 单条语音最多尝试几次（含首次）。
    /// </summary>
    /// <remarks>
    /// 实测：语音刚到达时立刻转写，QQ 侧 ASR 可能还没出结果（NapCat 回 <c>1200 获取语音转文字结果失败</c>），
    /// 隔一会儿再问就成功。所以**快速失败**会重试一次。
    /// 但**超时不重试**：超时说明 NapCat 侧整条链路卡住了，再问一轮只是把等待翻倍。
    /// </remarks>
    public int MaxAttempts { get; init; } = 2;

    /// <summary>重试前等待多久（毫秒）。</summary>
    public int RetryDelayMs { get; init; } = 700;

    /// <summary>
    /// 外部 ASR 回退路线上，<b>取音频</b>最多试几次（含首次）。默认 2。
    /// </summary>
    /// <remarks>
    /// 2026-11-28 实测（B00001 第二版）：语音**刚到达**时 NapCat 的 <c>get_record</c>
    /// 会一直等到超时（它按文件名去 QQ 的媒体索引里搜，而索引还没建好）；
    /// 同一秒之后重试一次只要 **28 ms** 就拿到 WAV。
    /// 所以「取不到就重试」不是保守，是**必需** —— 不重试的话，越新的语音越转不出来。
    /// </remarks>
    public int AudioFetchAttempts { get; init; } = 2;

    /// <summary>取音频重试的间隔（毫秒）。索引延迟是秒级，等太久反而拖慢消息。</summary>
    public int AudioFetchRetryDelayMs { get; init; } = 1_200;

    /// <summary>
    /// 取音频的**单次**超时（毫秒）。与 <see cref="TimeoutMs"/> 分开用：
    /// 成功只要几十毫秒，等待的意义只在"索引还没好"，所以单次不必给 8 秒。
    /// </summary>
    public int AudioFetchTimeoutMs { get; init; } = 5_000;
}
