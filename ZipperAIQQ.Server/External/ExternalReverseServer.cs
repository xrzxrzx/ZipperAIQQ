namespace ZipperAIQQ.Server.External;

/// <summary>反向 WS 监听块：外部应用连到 <c>host:port/ws/ext</c>。</summary>
/// <remarks>
/// host 由用户手填；启动时只绑定能解析成 IP / localhost 且 <see cref="Enabled"/> 为真的块，
/// 非法值只记警告，不会让整个服务起不来。
/// </remarks>
public sealed class ExternalReverseServer
{
    public string Name { get; init; } = "反向监听";

    public string Host { get; init; } = "0.0.0.0";

    public int Port { get; init; } = 5181;

    /// <summary>该监听块专用 token；空 = 用服务端主 token。</summary>
    public string? Token { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>正向 WS 目标块：本系统主动连到 <c>url</c>。</summary>
public sealed class ExternalForwardServer
{
    public string Name { get; init; } = "正向连接";

    public string Url { get; init; } = string.Empty;

    /// <summary>该目标专用 token；空 = 不带 Authorization。</summary>
    public string? Token { get; init; }

    public bool Enabled { get; init; } = true;
}
