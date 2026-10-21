namespace ZipperAIQQ.Storage.Personas;

/// <summary>一份人设。</summary>
/// <param name="Name">人设名（如「傲娇助手」），用于展示与切换。</param>
/// <param name="Content">人设正文，直接作为 system 消息内容。</param>
public sealed record Persona(string Name, string Content);

/// <summary>
/// 人设存储：**全局默认 + 每会话覆盖**。
/// </summary>
/// <remarks>
/// <para>
/// 需求 D12：每聊天的人设**完全覆盖**全局，不是叠加。
/// 叠加会让人设变成"两个角色的缝合怪"，比没有更糟。
/// </para>
/// <para>
/// 全局人设来自配置文件（部署时给），会话级覆盖来自数据库（运行时切）。
/// </para>
/// </remarks>
public interface IPersonaStore
{
    /// <summary>取该会话生效的人设：会话级优先，没有则回落全局。</summary>
    Task<Persona> GetAsync(string platform, string chatType, string chatId, CancellationToken ct = default);

    /// <summary>为某个会话设定人设（覆盖全局）。</summary>
    Task SetAsync(string sessionId, Persona persona, CancellationToken ct = default);

    /// <summary>清除会话级人设，回落到全局。</summary>
    Task ClearAsync(string sessionId, CancellationToken ct = default);
}
