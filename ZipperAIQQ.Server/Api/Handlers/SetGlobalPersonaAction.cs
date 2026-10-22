using System.Text;
using System.Text.Json;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Personas;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 写**全局人设**：直接改 <c>config/persona.md</c>。
/// </summary>
/// <remarks>
/// <para>
/// 之前只有会话级 <c>IPersonaStore.SetAsync</c>，全局人设只能手工改文件 ——
/// 客户端「人设」卡片看着像能改全局，其实存的是会话级（用户反馈"全局也保存不了"）。
/// </para>
/// <para>
/// payload：<c>{ "name": "小衣", "image": "...", "personality": "..." }</c>；
/// 两段都为空时拒绝（否则人设变空，AI 失去角色）。
/// </para>
/// </remarks>
public sealed class SetGlobalPersonaAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly ServerOptions _options;
    private readonly IConfigStore _configs;
    private readonly IPersonaStore _personas;

    public SetGlobalPersonaAction(ServerOptions options, IConfigStore configs, IPersonaStore personas)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(personas);

        _options = options;
        _configs = configs;
        _personas = personas;
    }

    public string Name => "setGlobalPersona";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 必须是对象。");
        }

        var image = PersonaHandlerHelper.ReadString(payload, "image");
        var personality = PersonaHandlerHelper.ReadString(payload, "personality");
        var name = PersonaHandlerHelper.ReadString(payload, "name");

        if (string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(personality))
        {
            throw new ApiException(ApiResponse.BadRequest, "image 与 personality 至少要给一个（都为空会让人设消失）。");
        }

        // name 为空时**沿用文件里原有的名字**（而不是塞一个技术默认名 "persona"）——
        // 否则客户端保存后读到 name="persona"，用户会以为"人设变回默认了"（实测踩到）。
        var effectiveName = name?.Trim();
        if (string.IsNullOrWhiteSpace(effectiveName))
        {
            effectiveName = "小衣";
        }

        var persona = Persona.FromParts(effectiveName, image, personality);

        var path = Path.GetFullPath(_options.PersonaPath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(path, persona.Content, new UTF8Encoding(false), ct).ConfigureAwait(false);

        // ⚠️ 关键：内存里的全局人设也要换掉，否则"写完再读"还是旧值
        // （DefaultPersonaStore 的 _global 是构造时固定的）
        if (_personas is DefaultPersonaStore store)
        {
            store.ReloadGlobal(persona);
        }

        await _configs
            .AppendAuditAsync(
                "setGlobalPersona",
                Actor,
                $"{path}（形象 {image?.Length ?? 0} 字 / 性格 {personality?.Length ?? 0} 字）",
                ct)
            .ConfigureAwait(false);

        return new
        {
            path,
            name = persona.Name,
            image = persona.Image,
            personality = persona.Personality,
            content = persona.Content,
            split = true,
        };
    }
}
