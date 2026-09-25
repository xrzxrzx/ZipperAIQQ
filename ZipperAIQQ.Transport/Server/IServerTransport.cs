using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport.Server;

public interface IServerTransport : IWebSocketTransport
{
    public void Initialize(string url);
    public void RegisterApiHandler(string apiName, Action<JsonNode> handler);
    public void UnregisterApiHandler(string apiName);
}
