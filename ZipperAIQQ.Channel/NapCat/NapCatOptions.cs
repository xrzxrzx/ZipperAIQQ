namespace ZipperAIQQ.Channel.NapCat;

/// <summary>
/// NapCat 通道的连接配置。
/// </summary>
/// <remarks>
/// 这些值属于**运行配置**（由客户端 UI 写入），不是引导配置。
/// 具体的主机地址与端口**不写进代码默认值**（FR-8.13）。
/// </remarks>
public sealed record NapCatOptions
{
    /// <summary>NapCat 正向 WS 的完整地址，如 <c>ws://127.0.0.1:17011</c>。</summary>
    public required string WebSocketUrl { get; init; }

    /// <summary>可选的鉴权 token（走 <c>Authorization: Bearer</c>）。</summary>
    public string? AccessToken { get; init; }

    /// <summary>连接超时（秒）。</summary>
    public int ConnectTimeoutSeconds { get; init; } = 15;

    /// <summary>API 调用（发送消息等）的响应超时（秒）。</summary>
    public int ApiTimeoutSeconds { get; init; } = 30;
}
