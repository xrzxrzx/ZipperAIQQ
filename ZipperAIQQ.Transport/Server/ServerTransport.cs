using Serilog;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport.Server;

/// <summary>
/// 仅支持单客户端
/// </summary>
public class ServerTransport : IServerTransport
{
    //Func<JsonNode, JsonNode>：接收一个JsonNode类型（接收到的原始JSON）和返回JsonNode类型（待响应给客户端的原始JSON）的Handler
    ConcurrentDictionary<string, Func<JsonNode, JsonNode>> _apiHandlers;

    public bool IsConnected => throw new NotImplementedException();
    public event Action<WebSocketTransportState>? StateChanged;
    public Func<JsonNode, string?>? GetAPIAction;//获取不到时返回空

    ILogger _logger;

    bool _isInitialized;
    bool _isDisposed;
    bool _isStartReceived;
    CancellationTokenSource _cts;
    CancellationToken _ct;
    Task? _receiveTask;

    HttpListener _listener;
    WebSocket? _socket;

    public ServerTransport(ILogger logger)
    {
        _apiHandlers = new ConcurrentDictionary<string, Func<JsonNode, JsonNode>>();

        _logger = logger;

        _isInitialized = false;
        _isDisposed = false;
        _cts = new CancellationTokenSource();
        _ct = _cts.Token;

        _listener = new HttpListener();
        _socket = null;
    }

    public void SetAPIActionGetter(Func<JsonNode, string?> getter)
    {
        GetAPIAction = getter;
    }

    public void Initialize(string url)
    {
        if (_isDisposed || _isInitialized)
            return;

        _listener.Prefixes.Add(url);
        _isInitialized = true;
    }

    public void Attach(WebSocket socket)
    {

    }

    public void RegisterApiHandler(string apiName, Func<JsonNode, JsonNode> handler)
    {
        if (_isDisposed)
            return;

        _apiHandlers[apiName] = handler;
    }

    public void UnregisterApiHandler(string apiName)
    {
        if (_isDisposed)
            return;

        _apiHandlers.TryRemove(apiName, out _);
    }

    public void StartReceive()
    {
        if (_isDisposed || _ct.IsCancellationRequested)
            return;

        _isStartReceived = true;

        _listener.Start();
        StateChanged?.Invoke(WebSocketTransportState.Connecting);

        _receiveTask = Task.Run(AcceptAsync);
    }

    /// <summary>
    /// 不做循环接受连接，只接受第一个
    /// </summary>
    /// <returns></returns>
    private async Task AcceptAsync()
    {
        while (!_ct.IsCancellationRequested)
        {
            var context = await _listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
            {
                HttpListenerWebSocketContext wsContext;
                try
                {
                    wsContext = await context.AcceptWebSocketAsync(null);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "接受WebSocket连接失 {Remote}", context.Request.RemoteEndPoint);
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                    continue;
                }

                _socket = wsContext.WebSocket;

                StateChanged?.Invoke(WebSocketTransportState.Connected);
                _logger.Information("接受连接 {Remote}", context.Request.RemoteEndPoint);

                await ReceiveLoopAsync();

                //重新进入等待连接状态，暂不设置成Disconnected状态，避免客户端断开后无法重新连接
                //break;
            }
            else
            {
                _logger.Warning("拒绝非WebSocket连接 {Remote}", context.Request.RemoteEndPoint);
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
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
                    return;
                }

                MessageArrived(message);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "接收消息异常");
            return;
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
    /// 收到的消息只会是API请求
    /// </summary>
    /// <param name="message"></param>
    private void MessageArrived(string message)
    {
        var json = JsonNode.Parse(message);
        if (json == null)
        {
            _logger.Warning("无法解析消息 {Message}", message);
            return;
        }

        var action = GetAPIAction!.Invoke(json);
        if (action == null)
        {
            _logger.Warning("收到非API请求消息 {Message}", message);
            return;
        }

        Func<JsonNode, JsonNode>? handler;
        _apiHandlers.TryGetValue(action, out handler);
        if(handler == null)
        {
            _logger.Warning("API {action} 未注册", action);
            return;
        }

        Task.Run(() => HandleAPI(json, handler));
    }

    private async void HandleAPI(JsonNode json, Func<JsonNode, JsonNode> handler)
    {
        var result = handler.Invoke(json);
        await SendAsync(result);
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

    public async Task CloseAsync(WebSocketCloseStatus status)
    {
        if (_isDisposed || _ct.IsCancellationRequested)
            return;

        StateChanged?.Invoke(WebSocketTransportState.Disconnecting);

        await _socket!.CloseAsync(status, null, _ct);
        _cts.Cancel();

        StateChanged?.Invoke(WebSocketTransportState.Disconnected);
    }

    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return ValueTask.CompletedTask;

        StateChanged?.Invoke(WebSocketTransportState.Disconnecting);

        _isDisposed = true;
        _cts.Dispose();

        _listener?.Stop();
        _listener?.Close();
        _socket?.Dispose();

        StateChanged?.Invoke(WebSocketTransportState.Disconnected);

        return ValueTask.CompletedTask;
    }
}