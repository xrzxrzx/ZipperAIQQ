using Serilog;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport;

public abstract class WebSocketDuplexBase : IWebSocketTransport
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
    /// 不确保发送成功
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public async Task SendAsync(JsonNode message)
    {
        if (_isDisposed || _ct.IsCancellationRequested)
            return;

        var jsonBytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        var buffer = new ArraySegment<byte>(jsonBytes);
        await _socket!.SendAsync(buffer, WebSocketMessageType.Text, true, _ct);
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
