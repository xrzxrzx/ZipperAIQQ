namespace ZipperAIQQ.Server.Api;

/// <summary>
/// action 内部主动拒绝（参数不合法 / 前置条件不满足）。
/// </summary>
/// <remarks>
/// <para>
/// 用它抛 <c>400</c>；其他异常一律被 dispatcher 当 <c>500</c> ——
/// 两者必须分开：客户端参数写错，不该被当成"服务端炸了"。
/// </para>
/// <para>
/// 需要让客户端做**二次确认**的场景（如「拉黑的管理员」）用 <c>409</c> + <see cref="Payload"/>：
/// 只给一句 message 的话，客户端还得自己猜是哪个账号冲突。
/// </para>
/// </remarks>
public sealed class ApiException : Exception
{
    public ApiException(int code, string message, object? payload = null)
        : base(message)
    {
        Code = code;
        Payload = payload;
    }

    public int Code { get; }

    /// <summary>结构化补充信息（可空），原样放进响应的 <c>data</c>。</summary>
    public object? Payload { get; }
}
