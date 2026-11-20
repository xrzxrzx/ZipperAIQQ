using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Serilog;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Server.External;

/// <summary>
/// 把一条 <see cref="WebSocket"/> 包成 <see cref="IDouyinTransport"/>。
/// </summary>
/// <remarks>
/// 只做"文本帧的收与发 + 对端描述"，不解释帧语义（协议在 <see cref="DouyinChannel"/> 里）——
/// 这样协议逻辑可以脱离网络单测（<c>DouyinChannelTests</c> 用的假连接就是这个原因）。
/// </remarks>
internal sealed class WsDouyinTransport : IDouyinTransport
{
    /// <summary>单帧上限：与反向端点同口径（<see cref="WsChannelEndpoint.MaxFrameBytes"/>）。</summary>
    private const int MaxFrameBytes = WsChannelEndpoint.MaxFrameBytes;

    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>收到的帧先进通道：端点与通道两侧的读写节奏不必对齐。</summary>
    private readonly Channel<string> _inbound = System.Threading.Channels.Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly Task _pump;

    public WsDouyinTransport(WebSocket socket, string peer, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(logger);

        _socket = socket;
        Peer = string.IsNullOrWhiteSpace(peer) ? "unknown" : peer;
        _logger = logger;
        _pump = Task.Run(PumpAsync);
    }

    /// <inheritdoc />
    public string Kind => "ws";

    /// <inheritdoc />
    public string Peer { get; }

    public async Task SendAsync(string json, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket
                .SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public IAsyncEnumerable<string> ReceiveAsync(CancellationToken ct) =>
        _inbound.Reader.ReadAllAsync(ct);

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_socket.State is WebSocketState.Closed or WebSocketState.Aborted)
        {
            return;
        }

        try
        {
            await _socket
                .CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 已断开的连接再关会抛 —— 关闭路径不该反过来炸调用方
            _logger.Debug(ex, "关闭 sidecar 连接时出错（忽略）");
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
    }

    /// <summary>读循环：把文本帧推进通道；连接关闭/出错就结束通道。</summary>
    private async Task PumpAsync()
    {
        var buffer = new byte[MaxFrameBytes];

        try
        {
            while (!_socket.CloseStatus.HasValue)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _inbound.Writer.TryComplete();
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > MaxFrameBytes)
                    {
                        _logger.Warning("sidecar 单帧超过 {Max} 字节，已丢弃该连接", MaxFrameBytes);
                        await _socket
                            .CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too big", CancellationToken.None)
                            .ConfigureAwait(false);
                        _inbound.Writer.TryComplete();
                        return;
                    }
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    await _inbound.Writer.WriteAsync(json).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "sidecar 连接读循环结束");
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
    }
}
