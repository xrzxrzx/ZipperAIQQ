using Serilog;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport;

public abstract class WebSocketDuplexBase : IWebSocketTransport, IResettableTransport
{
    protected ILogger _logger;

    protected bool _isDisposed;
    private CancellationTokenSource _cts;
    private CancellationToken _ct;
    protected WebSocket? _socket;

    private Task? _recvTask;
    private TaskCompletionSource _cs;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public Task Completion
    {
        get
        {
            if (_isDisposed)
                return Task.CompletedTask;
            return _cs.Task;
        }
    }

    public event Action<WebSocketTransportState>? StateChanged;

    public WebSocketDuplexBase(ILogger logger)
    {
        _logger = logger;
        _isDisposed = false;
        _cts = new CancellationTokenSource();
        _ct = _cts.Token;
        _socket = null;
        _recvTask = null;
        _cs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    protected void OnStateChanged(WebSocketTransportState state) => StateChanged?.Invoke(state);

    public void Attach(WebSocket socket)
    {
        if (_isDisposed)
        {
            _logger.Warning("WebSocket已被释放，无法附加新的WebSocket");
            return;
        }

        if (_socket != null || IsConnected)
        {
            _logger.Warning("WebSocket已存在，无法附加新的WebSocket");
            return;
        }

        if (socket.State != WebSocketState.Open)
        {
            _logger.Warning("WebSocket状态不为Open，无法附加新的WebSocket");
            return;
        }

        _socket = socket;

        _recvTask = ReceiveLoopAsync();
    }

    private async Task ReceiveLoopAsync()
    {
        if (_socket == null)
        {
            _logger.Error("WebSocket为空");
            return;
        }

        try
        {
            while (_socket.State == WebSocketState.Open && !_ct.IsCancellationRequested)
            {
                var message = await ReceiveFullMessage(_socket);
                if (message == null)
                {
                    _logger.Warning("接收消息为空，断开连接");
                    _cs.TrySetResult();
                    return;
                }

                try
                {
                    var json = JsonNode.Parse(message);
                    if (json == null)
                    {
                        _logger.Warning("无法解析消息 {Message}", message);
                        continue;
                    }

                    await OnMessageAsync(json);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "解析消息 {Message} 失败", message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("接收消息任务已取消");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "接收消息异常");
        }
        finally
        {
            _cs.TrySetResult();
        }
    }

    private async Task<string?> ReceiveFullMessage(WebSocket socket)
    {
        byte[] buffer = new byte[1024 * 4];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, _ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// 发送一帧；**状态不对就抛 <see cref="WebSocketException"/>**。
    /// </summary>
    /// <remarks>
    /// 旧实现状态不对时静默 return —— 上层以为发成功了，只能等到 echo 超时才失败，
    /// 而且分不清「根本没发出去」还是「发出去了没回」。11-05 的 <c>Aborted</c> 正是这样
    /// 被一路错判成「模型没按规矩来」，于是又烧了一次模型调用（问题记录 04 #7 第 1 步）。
    /// </remarks>
    public async Task SendAsync(JsonNode message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_isDisposed)
        {
            throw new WebSocketException("WebSocket 已被释放，无法发送");
        }

        var socket = _socket;
        if (socket is null)
        {
            throw new WebSocketException("WebSocket 尚未建立，无法发送");
        }

        if (socket.State != WebSocketState.Open)
        {
            throw new WebSocketException($"WebSocket 状态为 {socket.State}，无法发送");
        }

        if (_ct.IsCancellationRequested)
        {
            // 取消是调用方的决定，交给上层按 ct 判断 —— 不能记成通道故障。
            throw new OperationCanceledException(_ct);
        }

        var jsonBytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        var buffer = new ArraySegment<byte>(jsonBytes);
        await socket.SendAsync(buffer, WebSocketMessageType.Text, true, _ct);
    }

    /// <summary>
    /// 丢掉旧连接，为下一次 <c>ConnectAsync</c> 让路（可重复调用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 断线后 socket 会停在 <c>Aborted</c> 且**不会**变回 null；<c>Attach</c> 见到它就直接拒绝，
    /// 于是「重连」永远连不上。这里把它关掉、置空，并换一条新的取消源与完成信号。
    /// </para>
    /// <para>
    /// 顺序很重要：先取消接收循环 → 再 abort socket（避免关一半时它还在读）→ 最后换新 CTS。
    /// </para>
    /// </remarks>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        var socket = _socket;
        _socket = null;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放过：继续往下清
        }

        if (socket is not null)
        {
            try
            {
                socket.Abort();
                socket.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "释放旧 WebSocket 时出错（继续复位）");
            }
        }

        var recv = _recvTask;
        if (recv is not null && !recv.IsCompleted)
        {
            // 不等它也能继续；但等一小会儿能少一堆"读已释放对象"的噪声日志
            try
            {
                await Task.WhenAny(recv, Task.Delay(TimeSpan.FromSeconds(2), ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 调用方取消：复位本身已经做完了
            }
        }

        _recvTask = null;
        _isDisposed = false;
        _cts = new CancellationTokenSource();
        _ct = _cts.Token;
        _cs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        StateChanged?.Invoke(WebSocketTransportState.Disconnected);
    }

    protected abstract ValueTask OnMessageAsync(JsonNode json);

    public async Task CloseAsync(WebSocketCloseStatus status)
    {
        StateChanged?.Invoke(WebSocketTransportState.Disconnecting);

        if (_socket == null || _socket.State != WebSocketState.Open)
        {
            _cts.Cancel();
            return;
        }

        using var timeoutCts = new CancellationTokenSource(2000);
        try
        {
            await _socket.CloseAsync(status, null, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("关闭握手超时");
            _socket.Abort();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "关闭握手失败");
        }
        finally
        {
            _cts.Cancel();
        }

    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;

        await CloseAsync(WebSocketCloseStatus.NormalClosure);

        if (_recvTask != null)
        {
            await _recvTask;
        }

        _cs.TrySetResult();
        _cts.Dispose();
        _socket?.Dispose();

        StateChanged?.Invoke(WebSocketTransportState.Disconnected);
    }

}
