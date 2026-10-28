namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 「可登录平台通道」的窄接口：API / 启动流程只依赖它，不依赖具体通道类型。
/// </summary>
/// <remarks>
/// 抖音通道目前落在 Server 项目（历史原因），但登录相关的 HTTP action 不该
/// 直接注入 <c>DouyinChannel</c> —— 那样换实现 / 加第二个可登录平台都要改 action。
/// 运行时消息流仍走 <c>IMessageChannel</c>，这个接口只覆盖「登录态 + 二维码 + 登入登出」。
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

    Task RequestLoginAsync(CancellationToken ct = default);

    Task LogoutAsync(CancellationToken ct = default);
}
