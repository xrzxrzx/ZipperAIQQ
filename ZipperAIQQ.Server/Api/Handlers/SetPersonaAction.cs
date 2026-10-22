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

        await _personas.SetAsync(sessionId, persona, ct).ConfigureAwait(false);
        await _configs
            .AppendAuditAsync("setPersona", Actor, $"{sessionId}（形象 {image?.Length ?? 0} 字 / 性格 {personality?.Length ?? 0} 字）", ct)
            .ConfigureAwait(false);

        return new
        {
            sessionId,
            name = persona.Name,
            image = persona.Image,
            personality = persona.Personality,
            content = persona.Content,
            split = true,
        };
    }
}
