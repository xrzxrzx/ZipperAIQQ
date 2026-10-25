namespace ZipperAIQQ.Server.External;

/// <summary>
/// 反向 WS 监听块：外部应用连到 <c>host:port/ws/ext</c>。
/// </summary>
/// <remarks>
/// 只允许 <c>0.0.0.0</c>（对外）与 <c>127.0.0.1</c>（仅本机），避免误绑到不安全的网卡。
/// 端口变更需要重启服务端才生效（Kestrel 监听器在启动时建立）。
/// </remarks>
public sealed record ExternalReverseServer(string Host, int Port, string? Token)
{
    public static readonly IReadOnlyList<string> AllowedHosts = ["0.0.0.0", "127.0.0.1"];
}
