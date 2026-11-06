namespace ZipperAIQQ.Channel.Failures;

/// <summary>
/// 连接在，但**这一条**没发出去：等待 echo 超时、平台返回非 ok。
/// </summary>
/// <remarks>
/// 与 <see cref="ChannelUnavailableException"/> 的区别决定后续动作：
/// 不可用 → 停手等恢复；发送失败 → 可以退避重试这一条（但要计入失败计数）。
/// </remarks>
public sealed class ChannelSendFailedException : Exception
{
    public ChannelSendFailedException(string platform, string message, Exception? inner = null)
        : base(message, inner)
    {
        Platform = platform;
    }

    /// <summary>出问题的平台（<c>qq</c> / <c>douyin</c>）。</summary>
    public string Platform { get; }
}
