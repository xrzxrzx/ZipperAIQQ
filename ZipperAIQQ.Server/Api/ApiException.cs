namespace ZipperAIQQ.Server.Api;

/// <summary>
/// action 内部主动拒绝（参数不合法 / 前置条件不满足）。
/// </summary>
/// <remarks>
/// 用它抛 <c>400</c>；其他异常一律被 dispatcher 当 <c>500</c> ——
/// 两者必须分开：客户端参数写错，不该被当成"服务端炸了"。
/// </remarks>
public sealed class ApiException : Exception
{
    public ApiException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}
