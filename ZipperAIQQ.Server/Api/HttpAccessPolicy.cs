using System.Security.Cryptography;
using System.Text;

namespace ZipperAIQQ.Server.Api;

/// <summary>
/// 端点鉴权（架构 §3.5）：**loopback 免验；非 loopback 必须带 token**。
/// </summary>
/// <remarks>
/// 构造时就校验配置：非 loopback 又没 token → 抛异常，**拒绝启动**。
/// 这是「起得来但裸奔」与「起不来」之间唯一正确的选择 —— 前者没人会发现。
/// </remarks>
public sealed class HttpAccessPolicy
{
    private readonly bool _requireToken;
    private readonly byte[] _token;

    public HttpAccessPolicy(string listenAddress, string? token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenAddress);

        _requireToken = !IsLoopback(listenAddress);
        _token = Encoding.UTF8.GetBytes(token ?? string.Empty);

        if (_requireToken && string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"监听地址 '{listenAddress}' 不是 loopback，必须配置 ZIPPER_HTTP_TOKEN，" +
                "否则任何能连上端口的人都能下发命令。");
        }
    }

    /// <summary>是否强制要求 token（非 loopback 时 true）。</summary>
    public bool RequiresToken => _requireToken;

    /// <summary>请求是否放行。</summary>
    public bool IsAllowed(string? providedToken)
    {
        if (!_requireToken)
        {
            return true;
        }

        if (string.IsNullOrEmpty(providedToken))
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(providedToken);

        // 定长比较：避免按字节提前返回造成的时序侧信道
        return provided.Length == _token.Length &&
               CryptographicOperations.FixedTimeEquals(provided, _token);
    }

    /// <summary>地址是不是 loopback（只认 127.0.0.1 / localhost / ::1）。</summary>
    public static bool IsLoopback(string listenAddress)
    {
        var idx = listenAddress.LastIndexOf(':');
        var host = idx > 0 ? listenAddress[..idx] : listenAddress;
        host = host.Trim('[', ']').Trim();

        return host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }
}
