namespace ZipperAIQQ.Channel.Failures;

/// <summary>
/// 一次失败**到底是谁的错**（问题记录 04 #7 的底座）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：通道发不出去时，上层过去只能看到 <c>TimeoutException</c> /
/// <c>InvalidOperationException</c> / <c>WebSocketException</c> 这些**形状**，
/// 分不清「通道断了」「目标不存在」还是「内容被拒」，于是统一当成「模型没按规矩来」，
/// 再叫一次模型 —— 钱就这么烧掉的（11-05 ¥28.84）。
/// </para>
/// <para>
/// 判定顺序：**先语义异常，再看类型**。通道实现应该在抛出点就把已知情况包成
/// <see cref="ChannelUnavailableException"/> / <see cref="ChannelSendFailedException"/>；
/// 分类器只负责兜底识别底层异常类型。
/// </para>
/// </remarks>
public enum FailureKind
{
    /// <summary>认不出来。**不要拿它当「没事」** —— 该补分类的地方要补。</summary>
    Unknown = 0,

    /// <summary>通道不可用：连接没建立 / 已断开 / socket Aborted。</summary>
    ChannelUnavailable = 1,

    /// <summary>连接在，但这一条没发出去：等待 echo 超时、协议返回非 ok。</summary>
    ChannelSendFailed = 2,

    /// <summary>目标非法：会话不存在、平台未注册、session 格式错。</summary>
    TargetInvalid = 3,

    /// <summary>被限流（HTTP 429 / 平台频控）。</summary>
    RateLimited = 4,

    /// <summary>模型侧瞬时故障：5xx、网络抖动（重试有意义）。</summary>
    ModelTransient = 5,

    /// <summary>模型侧硬失败：4xx（429 除外）、报文结构不符（重试没意义）。</summary>
    ModelFatal = 6,

    /// <summary>内容被平台拒：空内容、超长、敏感词。</summary>
    ContentInvalid = 7,
}
