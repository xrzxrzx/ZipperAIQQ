namespace ZipperAIQQ.Channel.Failures;

/// <summary>
/// 内容被拒：空内容、超长、被平台判为敏感。
/// </summary>
/// <remarks>
/// 重试同一条没有意义 —— 要么改内容，要么别发。与 <see cref="ChannelSendFailedException"/>
/// 的区别就在这里：后者可以退避重试。
/// </remarks>
public sealed class ContentInvalidException : Exception
{
    public ContentInvalidException(string platform, string message, Exception? inner = null)
        : base(message, inner)
    {
        Platform = platform;
    }

    /// <summary>出问题的平台（<c>qq</c> / <c>douyin</c>）。</summary>
    public string Platform { get; }
}
