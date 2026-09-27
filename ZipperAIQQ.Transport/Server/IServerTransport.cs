using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport.Server;

public interface IServerTransport : IWebSocketTransport
{
    void SetAPIActionGetter(Func<JsonNode, string?> getter);

    void Initialize(string url);
    void RegisterApiHandler(string apiName, Func<JsonNode, JsonNode> handler);
    void UnregisterApiHandler(string apiName);
}
