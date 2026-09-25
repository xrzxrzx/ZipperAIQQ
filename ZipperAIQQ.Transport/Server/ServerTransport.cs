using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport.Server;

/// <summary>
/// 仅支持但客户端
/// </summary>
public class ServerTransport : IServerTransport
{
    ConcurrentDictionary<string, Action<JsonNode>> _apiHandlers;

    public bool IsConnected => throw new NotImplementedException();
    public event Action<WebSocketTransportState>? StateChanged;

    bool _isInitialized;
    bool _isDisposed;
    CancellationTokenSource _cts;
    CancellationToken _ct;

    HttpListener _listener;

    public ServerTransport()
    {
        _apiHandlers = new ConcurrentDictionary<string, Action<JsonNode>>();

        _isInitialized = false;
        _isDisposed = false;
        _cts = new CancellationTokenSource();
        _ct = _cts.Token;

        _listener = new HttpListener();
    }

    public void Initialize(string url)
    {
        if (_isDisposed || _isInitialized)
            return;

        _listener.Prefixes.Add(url);
        _isInitialized = true;
    }

    public void RegisterApiHandler(string apiName, Action<JsonNode> handler)
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

        _listener.Start();

        Task.Run(AcceptAsync);
    }

    /// <summary>
    /// 不做循环接受连接，只接受第一个
    /// </summary>
    /// <returns></returns>
    private async Task AcceptAsync()
    {
        while (_ct.IsCancellationRequested)
        {
            var context = await _listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
            {
                break;
            }
            else
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
    }

    private Task ReceiveLoopAsync()
    {

    }

    public Task SendAsync(JsonNode message)
    {
        if (_isDisposed || _ct.IsCancellationRequested)
            return Task.CompletedTask;

        throw new NotImplementedException();
    }

    public Task CloseAsync(WebSocketCloseStatus status)
    {
        if (_isDisposed || _ct.IsCancellationRequested)
            return Task.CompletedTask;

        throw new NotImplementedException();
    }

    public void CancelAction()
    {
        if (_isDisposed || _ct.IsCancellationRequested)
            return;

        _cts.Cancel();
    }

    public void ResetAction()
    {
        if (_isDisposed || !_ct.IsCancellationRequested)
            return;

        _cts.TryReset();
    }

    public ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return ValueTask.CompletedTask;

        _isDisposed = true;
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
