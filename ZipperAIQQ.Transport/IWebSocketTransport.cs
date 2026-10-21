using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport;

public enum WebSocketTransportState
{
    Connecting,//仅客户端
    Connected,//仅客户端
    Disconnecting,
    Disconnected,
}

public interface IWebSocketTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task Completion { get; }

    event Action<WebSocketTransportState>? StateChanged;

    void Attach(WebSocket socket);

    Task SendAsync(JsonNode message);
    Task CloseAsync(WebSocketCloseStatus status);
}
