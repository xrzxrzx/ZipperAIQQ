using System.Text.Json;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Personas;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 清掉会话级人设，让它回落到全局。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要：会话级人设是**覆盖**全局的（<c>override ?? global</c>）。
/// 一旦给某个会话设过人设，想"改回跟全局一样"就只能删掉那条记录 ——
/// 之前没有这个入口，只能手工改库。
/// </para>
/// <para>
/// payload：<c>{ "sessionId": "qq:p123456" }</c>；传 <c>"global"</c> 不支持（全局用 setGlobalPersona 改）。
/// </para>
/// </remarks>
public sealed class ClearPersonaAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly IPersonaStore _personas;
    private readonly IConfigStore _configs;

    public ClearPersonaAction(IPersonaStore personas, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(personas);
        ArgumentNullException.ThrowIfNull(configs);

        _personas = personas;
        _configs = configs;
    }

    public string Name => "clearPersona";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var (_, _, _, sessionId) = PersonaHandlerHelper.ParseSession(payload);

        if (string.Equals(sessionId, "global", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                ApiResponse.BadRequest,
                "不支持清除全局人设（那会让 AI 失去角色）。要改全局请用 setGlobalPersona。");
        }

        await _personas.ClearAsync(sessionId, ct).ConfigureAwait(false);

        await _configs
            .AppendAuditAsync("clearPersona", Actor, sessionId, ct)
            .ConfigureAwait(false);

        return new { sessionId, cleared = true, fallback = "global" };
    }
}
