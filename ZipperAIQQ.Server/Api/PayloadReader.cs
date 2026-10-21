using System.Text.Json;

namespace ZipperAIQQ.Server.Api;

/// <summary>从 <see cref="JsonElement"/> payload 里安全取值。</summary>
/// <remarks>
/// payload 可能是 null / 不是对象 / 字段类型不对 —— 全部按"没给"处理，
/// 由各 action 决定默认值或报 <c>400</c>。
/// </remarks>
public static class PayloadReader
{
    public static int GetInt(JsonElement payload, string name, int fallback)
    {
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : fallback;
    }

    public static string? GetString(JsonElement payload, string name)
    {
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
