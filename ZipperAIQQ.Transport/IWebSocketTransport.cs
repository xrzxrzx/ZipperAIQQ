using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport;

public enum WebSocketTransportState
{
    Connecting,
    Connected,
    Disconnecting,
    Disconnected,
}

public interface IWebSocketTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    event Action<WebSocketTransportState>? StateChanged;

    Task SendAsync(JsonNode message);
    Task StartReceive();
    Task CloseAsync(WebSocketCloseStatus status);
    void CancelAction();
    void ResetAction();
}
