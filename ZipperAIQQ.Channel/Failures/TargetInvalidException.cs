namespace ZipperAIQQ.Channel.Failures;

/// <summary>
/// 目标非法：会话不存在、平台未注册、session 格式不对。
/// </summary>
/// <remarks>
/// 与「通道挂了」分开：通道好好的，换一个目标就能发；也不该重试同一条。
/// </remarks>
public sealed class TargetInvalidException : Exception
{
    public TargetInvalidException(string platform, string message, Exception? inner = null)
        : base(message, inner)
    {
        Platform = platform;
    }

    /// <summary>出问题的平台（<c>qq</c> / <c>douyin</c>）。</summary>
    public string Platform { get; }
}
