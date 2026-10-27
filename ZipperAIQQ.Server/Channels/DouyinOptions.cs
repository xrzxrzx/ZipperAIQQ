namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 抖音 sidecar 配置（<c>config/server.yaml</c> 的 <c>douyin</c> 节）。
/// </summary>
/// <remarks>
/// <para>
/// sidecar 是独立 Node 进程（<c>douyin-im</c> 是 Node 22.5+ / ESM / <c>node:sqlite</c>，.NET 无法直接调用）。
/// 它**主动连入** Server 的 <c>/ws/channel</c>，Server 不主动连它 —— 省掉 sidecar 开监听端口与端口分配，
/// 且 sidecar 重启能自动重连。
/// </para>
/// <para>
/// 默认 <see cref="Enabled"/> = false：没配 sidecar 时服务照常跑，只是多平台里少一个平台。
/// </para>
/// </remarks>
public sealed record DouyinOptions
{
    /// <summary>总开关（关掉时 <c>/ws/channel</c> 直接 404，不占资源）。</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// <c>/ws/channel</c> 的 Bearer token。
    /// </summary>
    /// <remarks>
    /// 非 loopback 监听时**必须**配（与 <c>ExternalReverseServer</c> 同口径）；
    /// **不落日志、不回显**。
    /// </remarks>
    public string? Token { get; init; }

    /// <summary>允许的会话白名单（空 = 全部；支持 <c>douyin:p*</c> / <c>douyin:g*</c> 前缀）。</summary>
    public IReadOnlyList<string> AllowedSessions { get; init; } = [];

    /// <summary>出站 ack 超时（毫秒）。超时即清理 pending，不泄漏。</summary>
    public int SendTimeoutMs { get; init; } = 15_000;

    /// <summary>文本分段阈值（与 douyin-im 对齐：单条 ≤ 16000 字符）。</summary>
    public int MaxTextLength { get; init; } = 16_000;

    /// <summary>查询（<c>query.contacts</c> / <c>query.groups</c>）超时（毫秒）。</summary>
    public int QueryTimeoutMs { get; init; } = 10_000;
}
