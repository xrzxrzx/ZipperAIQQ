namespace ZipperAIQQ.Channel.Failures;

/// <summary>
/// 通道**不可用**：连接没建立、已断开、socket 进入 Aborted。
/// </summary>
/// <remarks>
/// 这是「语义异常」：抛出点已经知道原因，上层不该再靠异常类型或文案去猜。
/// 收到它的调用方应当**停止重试与调用模型**，转去做恢复/上报（问题记录 04 #7）。
/// </remarks>
public sealed class ChannelUnavailableException : Exception
{
    public ChannelUnavailableException(string platform, string message, Exception? inner = null)
        : base(message, inner)
    {
        Platform = platform;
    }

    /// <summary>出问题的平台（<c>qq</c> / <c>douyin</c>）。</summary>
    public string Platform { get; }
}
