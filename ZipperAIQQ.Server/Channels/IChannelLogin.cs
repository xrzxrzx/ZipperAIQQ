namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 「可登录平台通道」的窄接口：API / 启动流程只依赖它，不依赖具体通道类型。
/// </summary>
/// <remarks>
/// 抖音通道目前落在 Server 项目（历史原因），但登录相关的 HTTP action 不该
/// 直接注入 <c>DouyinChannel</c> —— 那样换实现 / 加第二个可登录平台都要改 action。
/// 运行时消息流仍走 <c>IMessageChannel</c>，这个接口只覆盖
/// 「登录态 + 二维码 + 登入登出 + 底座运营信息（驱动 / 挑战 / 页面体征）」。
/// </remarks>
public interface IChannelLogin
{
    /// <summary>通道是否已连上（sidecar / 协议端在线）。</summary>
    bool IsConnected { get; }

    /// <summary>机器人自身账号（未登录时为 <c>null</c>）。</summary>
    string? SelfId { get; }

    /// <summary>登录状态（<c>Unknown</c> / <c>WaitingQr</c> / <c>Online</c> …）。</summary>
    string LoginState { get; }

    /// <summary>登录失败 / 等待中的说明。</summary>
    string? LoginMessage { get; }

    /// <summary>登录二维码（base64，仅内存）。</summary>
    string? LoginQrBase64 { get; }

    /// <summary>二维码生成时间。</summary>
    DateTimeOffset? LoginQrAt { get; }

    /// <summary>已接受 sidecar 的驱动（<c>im</c> / <c>web</c>）；未握手为 <c>null</c>。</summary>
    string? Driver { get; }

    /// <summary>最近一次连接被拒的原因（驱动不匹配时有值）。</summary>
    string? DriverRejectNote { get; }

    /// <summary>最近一次浏览器体征（<c>browser.state</c>）；旧 sidecar 不带 → <c>null</c>。</summary>
    DouyinBrowserState? BrowserState { get; }

    /// <summary>
    /// 当前等待用户回填的登录挑战（短信 / 滑块）；没有则为 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 浏览器底座的登录态是 profile + cookie，扫码之外还可能被要求过验证码 / 滑块；
    /// 这个属性把「现在卡在哪一步」暴露给客户端（否则用户只看到状态停在等待扫码）。
    /// </remarks>
    DouyinLoginChallenge? Challenge { get; }

    Task RequestLoginAsync(CancellationToken ct = default);

    Task LogoutAsync(CancellationToken ct = default);

    /// <summary>
    /// 回填挑战答案（短信码 / 滑块偏移）。
    /// </summary>
    /// <param name="kind">挑战类型（见 <see cref="DouyinChallengeKind"/>）。</param>
    /// <param name="value">用户答案。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>通道没连上 / 当前没有待回填的挑战时抛异常（由 API 转成可读错误，不静默丢）。</remarks>
    Task SubmitChallengeAsync(string kind, string value, CancellationToken ct = default);
}
