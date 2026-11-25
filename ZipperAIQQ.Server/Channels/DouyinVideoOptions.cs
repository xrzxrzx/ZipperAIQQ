namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 抖音视频理解（B4）的运行参数（<c>douyin</c> 配置节的 <c>video*</c> 键）。
/// </summary>
/// <remarks>
/// <para>
/// 键名**摊平成两级**（<c>douyin.videoMaxFrames</c>，不是 <c>douyin.video.maxFrames</c>）：
/// 加载器只支持「section → key」两级，三级要改加载器（与 B2-b 的 <c>ai.*</c> 同理）。
/// </para>
/// <para>
/// 这里是**唯一真相**：sidecar 只兜硬上限（单帧 1 MiB / 本体 96 MiB），
/// 真正的策略数字（抽几张、窗口多长、最长转写多久）都由服务端随请求下发。
/// </para>
/// </remarks>
public sealed record DouyinVideoOptions
{
    /// <summary>
    /// 是否把 <c>douyin_video</c> / <c>douyin_transcript</c> 给模型（默认开）。
    /// </summary>
    /// <remarks>
    /// 关的是「工具存不存在」：没装浏览器底座的部署本来就连不上，工具自然会因为
    /// <c>IsReady=false</c> 不出现 —— 这个开关是给「底座能用但不想让它烧流量」的场合。
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>等分 / 区间抽帧一次最多几张（默认 6，硬顶 16；方案 Q-4）。</summary>
    /// <remarks>
    /// 每张图都要进上下文当视觉输入：给太多会挤掉对话本身，还容易被模型挑错张数。
    /// </remarks>
    public int MaxFrames { get; init; } = 6;

    /// <summary>抽帧时的最大宽度（默认 720，硬顶 1080）。</summary>
    /// <remarks>
    /// 竖版 1080×1920 整幅 JPEG 就有 150–200 KB，6 张直接顶到回传上限；
    /// 缩到 720 宽后每张 40–80 KB，且模型认出「图里写了什么」完全够。
    /// </remarks>
    public int FrameMaxWidth { get; init; } = 720;

    /// <summary>视频本体下载上限（MiB，默认 64，硬顶 96）。</summary>
    public int MaxVideoMb { get; init; } = 64;

    /// <summary>单次媒体请求超时（秒，默认 60）。</summary>
    /// <remarks>下载 + 解码 + 抽帧是一整串动作，比普通查询慢得多，不能复用 <c>douyin.queryTimeoutMs</c>。</remarks>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// 单次转写最多覆盖多少秒（默认 180，硬顶 600）。
    /// </summary>
    /// <remarks>
    /// 超了只转**前 N 秒**并如实标注。为什么要上限：抽音按 20 秒一个窗口回传，
    /// 一条 30 分钟的视频就是 90 个窗口 —— 时间和 token 都不是模型该随手决定的。
    /// </remarks>
    public int MaxAudioSeconds { get; init; } = 180;

    /// <summary>
    /// 抽音窗口长度（秒，默认 20，硬顶 60）。
    /// </summary>
    /// <remarks>
    /// 20 秒 16 kHz 单声道 = 640 KB，base64 后约 853 KB ——
    /// 正好卡在 <c>/ws/channel</c> 的 1 MiB 单帧上限之内。调大会被端点直接掐断连接。
    /// </remarks>
    public int AudioWindowSeconds { get; init; } = 20;
}
