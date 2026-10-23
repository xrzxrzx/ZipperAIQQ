using System.Text.Json;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Personas;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 写人设（第 21 项）：形象 / 性格分别保存，服务端拼成完整正文。
/// </summary>
/// <remarks>
/// payload：<c>{ "sessionId": "qq:p123", "name": "小衣", "image": "...", "personality": "..." }</c>
/// <para>
/// 两者可以只给一个；**都为空时拒绝**（否则人设变成空字符串，AI 会失去角色）。
/// </para>
/// </remarks>
public sealed class SetPersonaAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly IPersonaStore _personas;
    private readonly IConfigStore _configs;

    public SetPersonaAction(IPersonaStore personas, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(personas);
        ArgumentNullException.ThrowIfNull(configs);

        _personas = personas;
        _configs = configs;
    }

    public string Name => "setPersona";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var (platform, chatType, chatId, sessionId) = PersonaHandlerHelper.ParseSession(payload);

        var name = PersonaHandlerHelper.ReadString(payload, "name");
        var image = PersonaHandlerHelper.ReadString(payload, "image");
        var personality = PersonaHandlerHelper.ReadString(payload, "personality");

        if (string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(personality))
        {
            throw new ApiException(ApiResponse.BadRequest, "image 与 personality 至少要给一个（都为空会让人设消失）。");
        }

        var current = await _personas.GetAsync(platform, chatType, chatId, ct).ConfigureAwait(false);
        var persona = Persona.FromParts(
            string.IsNullOrWhiteSpace(name) ? current.Name : name.Trim(),
            image,
            personality);

        // #16：enabled 可单独提交（只切开关时不必重传正文）
        var state = await _personas.GetStateAsync(sessionId, ct).ConfigureAwait(false);
        var enabled = payload.TryGetProperty("enabled", out var enabledElement) &&
                      enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? enabledElement.GetBoolean()
            : state.Enabled;

        // #10：内容与开关都没变 → 不写库、不写审计
        var unchanged = state.Override is not null &&
                        state.Override.Content == persona.Content &&
                        state.Override.Name == persona.Name &&
                        state.Enabled == enabled;

        if (unchanged)
        {
            return new
            {
                sessionId,
                name = persona.Name,
                image = persona.Image,
                personality = persona.Personality,
                content = persona.Content,
                split = true,
                enabled,
                hasOverride = true,
                updated = 0,
                skipped = true,
            };
        }

        await _personas.SetAsync(sessionId, persona, ct).ConfigureAwait(false);
        await _personas.SetEnabledAsync(sessionId, enabled, ct).ConfigureAwait(false);

        await _configs
            .AppendAuditAsync("setPersona", Actor, $"{sessionId}（形象 {image?.Length ?? 0} 字 / 性格 {personality?.Length ?? 0} 字 / 启用 {enabled}）", ct)
            .ConfigureAwait(false);

        return new
        {
            sessionId,
            name = persona.Name,
            image = persona.Image,
            personality = persona.Personality,
            content = persona.Content,
            split = true,
            enabled,
            hasOverride = true,
            updated = 1,
            skipped = false,
        };
    }
}
