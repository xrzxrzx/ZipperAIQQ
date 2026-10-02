using System.Text.Json.Nodes;

namespace ZipperAIQQ.Transport.Server;

public interface IServerTransport : IWebSocketTransport
{
    void SetAPIActionGetter(Func<JsonNode, string?> getter);
    void SetUnknownActionResponseGetter(Func<JsonNode, JsonNode> getter);

    void RegisterApiHandler(string apiName, Func<JsonNode, Task<JsonNode>> handler);
    void UnregisterApiHandler(string apiName);
}
