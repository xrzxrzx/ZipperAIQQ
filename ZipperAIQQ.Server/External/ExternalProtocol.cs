using System.Text.Json;

namespace ZipperAIQQ.Server.External;

/// <summary>外部 WS 的帧协议（与 <c>docs/05-契约/external-ws.md</c> 保持一致）。</summary>
public static class ExternalProtocol
{
    public const string Version = "1";

    public static string Frame(string type, object? data) =>
        JsonSerializer.Serialize(new
        {
            type,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data,
        });

    public static string Event(string kind, string session, object payload) =>
        JsonSerializer.Serialize(new
        {
            type = "event",
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data = new { kind, session, payload },
        });

    public static string Ack(string? requestId, bool ok, string? error = null) =>
        JsonSerializer.Serialize(new
        {
            type = "ack",
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data = new { requestId, ok, error },
        });

    /// <summary>解析客户端发来的 JSON；失败返回 false + 中文原因。</summary>
    public static bool TryParse(string json, out JsonDocument document, out string? error)
    {
        try
        {
            document = JsonDocument.Parse(json);
            error = null;
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            error = "不是合法 JSON。";
            return false;
        }
    }

    public static bool TryGetString(JsonElement root, string name, out string? value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        value = null;
        return false;
    }
}
