using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Serilog;

namespace ZipperAIQQ.Transport.Client;

/// <summary>
/// <see cref="IClientTransport"/> 的实现。
/// </summary>
/// <remarks>
/// <para>
/// 只负责「连上 + 收发 + 关闭」，**不做自动重连** —— 重连策略属于上层通道（NapCatChannel）的职责，
/// 因为它还涉及「重连后要不要重新同步状态」，那是业务语义，不是传输层该管的。
/// </para>
/// <para>
/// 收到的每一帧交给 <see cref="EventHandler"/>；未设置时只记日志，不抛异常
/// —— 一帧没人处理不该把整个连接干崩。
/// </para>
/// </remarks>
public sealed class ClientTransport : WebSocketDuplexBase, IClientTransport
{
    /// <summary>收到消息时的回调。由上层（通道实现）注册。</summary>
    public Func<JsonNode, ValueTask>? EventHandler { get; set; }

    public ClientTransport(ILogger logger) : base(logger)
    {
    }

    public async Task ConnectAsync(Uri uri, string? accessToken = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (uri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException($"WebSocket 地址必须以 ws:// 或 wss:// 开头，实际为 {uri.Scheme}://", nameof(uri));
        }

        var socket = new ClientWebSocket();

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        }

        OnStateChanged(WebSocketTransportState.Connecting);

        try
        {
            await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            OnStateChanged(WebSocketTransportState.Disconnected);
            throw;
        }

        // Attach 会启动接收循环；它自己会校验 socket 状态
        Attach(socket);
        OnStateChanged(WebSocketTransportState.Connected);

        _logger.Information("已连接到 {Uri}", uri);
    }

    protected override ValueTask OnMessageAsync(JsonNode json)
    {
        var handler = EventHandler;
        if (handler is null)
        {
            _logger.Warning("收到消息但未注册处理器，已丢弃");
            return ValueTask.CompletedTask;
        }

        return handler(json);
    }
}
