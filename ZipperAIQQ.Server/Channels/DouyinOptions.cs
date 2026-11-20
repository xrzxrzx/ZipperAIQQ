namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 抖音通道的**底座驱动**（<c>douyin.driver</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 两个 sidecar 都往同一个 <c>/ws/channel</c> 反连，服务端靠 <c>hello.driver</c> 区分：
/// <c>im</c> = <c>douyin-im</c> 底座（旧，其 sidecar 已于 2026-11-21 从仓库移除，仅作回退档位保留），
/// <c>web</c> = <c>douyin-web-sidecar</c>（Playwright + 网页版，新）。
/// </para>
/// <para>
/// 这是**回退开关**：平台按版本拒发（<c>check=10509</c>）时切到 <c>web</c>；
/// 万一浏览器侧不稳，把值改回 <c>im</c> 起旧 sidecar 即可恢复（实施方案 B1 验收 A-5）。
/// </para>
/// <para>
/// 自报缺 <c>driver</c> 的老 sidecar 一律按 <see cref="Im"/> 处理（向后兼容）。
/// </para>
/// </remarks>
public static class DouyinDriver
{
    /// <summary>`douyin-im` 底座（旧）。</summary>
    public const string Im = "im";

    /// <summary>浏览器底座（新）。</summary>
    public const string Web = "web";

    /// <summary>是否认得的驱动值（大小写不敏感）。</summary>
    public static bool IsKnown(string? driver) =>
        string.Equals(driver, Im, StringComparison.OrdinalIgnoreCase)
        || string.Equals(driver, Web, StringComparison.OrdinalIgnoreCase);

    /// <summary>规范化成小写；认不出返回 `null`（调用方决定回退谁）。</summary>
    public static string? Normalize(string? driver) =>
        IsKnown(driver) ? driver!.Trim().ToLowerInvariant() : null;
}

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

    /// <summary>出站 ack 超时（毫秒）。超时即清理 pending，不泄漏。</summary>
    public int SendTimeoutMs { get; init; } = 15_000;

    /// <summary>文本分段阈值（与 douyin-im 对齐：单条 ≤ 16000 字符）。</summary>
    public int MaxTextLength { get; init; } = 16_000;

    /// <summary>查询（<c>query.contacts</c> / <c>query.groups</c>）超时（毫秒）。</summary>
    public int QueryTimeoutMs { get; init; } = 10_000;

    /// <summary>
    /// 期望的底座驱动（见 <see cref="DouyinDriver"/>）：只接受自报一致的 sidecar。
    /// </summary>
    /// <remarks>
    /// 默认 <see cref="DouyinDriver.Im"/> —— 切换是**显式动作**，不在升级时替用户换底座（实施方案 §6.4）。
    /// </remarks>
    public string Driver { get; init; } = DouyinDriver.Im;
}
