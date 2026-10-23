using System.Text.Json;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Personas;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 读人设（第 21 项）：形象 / 性格分开返回，便于客户端分别编辑。
/// </summary>
/// <remarks>
/// 老数据（没有小节标头的人设）两个字段都为 <c>null</c>，
/// 客户端据此提示"这是旧格式人设，正文整体生效"。
/// </remarks>
public sealed class GetPersonaAction : IApiAction
{
    private readonly IPersonaStore _personas;

    public GetPersonaAction(IPersonaStore personas)
    {
        ArgumentNullException.ThrowIfNull(personas);
        _personas = personas;
    }

    public string Name => "getPersona";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var (platform, chatType, chatId, sessionId) = PersonaHandlerHelper.ParseSession(payload);

        var persona = await _personas.GetAsync(platform, chatType, chatId, ct).ConfigureAwait(false);
        var state = sessionId == "global"
            ? new PersonaState(null, true)
            : await _personas.GetStateAsync(sessionId, ct).ConfigureAwait(false);

        return new
        {
            sessionId,
            name = persona.Name,
            image = persona.Image,
            personality = persona.Personality,
            content = persona.Content,
            split = persona.Image is not null || persona.Personality is not null,

            // #16：会话级预设的启用状态；全局永远是「没有覆盖」
            enabled = state.Enabled,
            hasOverride = state.Override is not null,
        };
    }
}

/// <summary>会话标识解析：<c>qq:p&lt;id&gt;</c> / <c>qq:g&lt;id&gt;</c>。</summary>
internal static class PersonaHandlerHelper
{
    public static (string Platform, string ChatType, string ChatId, string SessionId) ParseSession(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("sessionId", out var element) ||
            element.GetString() is not { Length: > 3 } sessionId)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 sessionId（如 qq:p123456），或 \"global\" 表示全局人设。");
        }

        // "global" = 读全局人设：给一组查不到会话级记录的 key，GetAsync 自然回落全局
        if (string.Equals(sessionId, "global", StringComparison.OrdinalIgnoreCase))
        {
            return (string.Empty, string.Empty, string.Empty, "global");
        }

        var parts = sessionId.Split(':', 2);
        if (parts.Length != 2 || parts[1].Length < 2)
        {
            throw new ApiException(ApiResponse.BadRequest, $"看不懂的 sessionId：{sessionId}。");
        }

        var prefix = parts[1][0];
        var id = parts[1][1..];

        return (parts[0], prefix == 'g' ? "Group" : "Private", id, sessionId);
    }

    public static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
