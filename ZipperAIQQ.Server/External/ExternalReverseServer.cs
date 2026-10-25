namespace ZipperAIQQ.Server.External;

/// <summary>反向 WS 监听块：外部应用连到 <c>host:port/ws/ext</c>。</summary>
/// <remarks>
/// host 由用户手填（不限制成固定几项）；启动时只绑定能解析成 IP / localhost 的值，
/// 非法值只记警告，不会让整个服务起不来。
/// </remarks>
public sealed record ExternalReverseServer(string Host, int Port, string? Token);

/// <summary>正向 WS 目标块：本系统主动连到 <c>url</c>。</summary>
public sealed record ExternalForwardServer(string Url, string? Token);
