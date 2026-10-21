namespace ZipperAIQQ.Storage.Config;

/// <summary>
/// 一个具名模型端点的**运行配置**（FR-8.10）。
/// </summary>
/// <param name="ApiKind">0 = Responses，1 = ChatCompletions（与 Agent 侧的枚举数值对齐）。</param>
/// <param name="KeyCipher">**加密后**的 key；明文永不入库（FR-8.6）。</param>
public sealed record ModelEndpointSetting(
    string Name,
    string BaseUrl,
    string Model,
    int ApiKind,
    string? KeyCipher,
    DateTimeOffset UpdatedAt);

/// <summary>端点 → 角色的映射（FR-8.11）：<c>Chat</c> / <c>Judge</c>。</summary>
public sealed record RoleBinding(string Role, string EndpointName);

/// <summary>审计记录（FR-8.9）：**记动作，不记内容**。</summary>
public sealed record AuditEntry(long Id, string Action, string Actor, string? Target, DateTimeOffset At);

/// <summary>
/// 运行配置仓储（M2-B）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>ServerOptions</c> 的分工（FR-8.1 / 8.2）：
/// 引导配置（监听地址 / token / 密钥环路径）只在环境变量里；
/// **其余全部（含 LLM key）在库里**，只由客户端通过 API 写入。
/// </para>
/// <para>
/// key 到这里时**已经是密文**：加密/解密在 Server 层做，
/// Storage 不认识明文，也就不会不小心把它写进日志。
/// </para>
/// </remarks>
public interface IConfigStore
{
    Task<IReadOnlyList<ModelEndpointSetting>> ListEndpointsAsync(CancellationToken ct = default);

    Task<ModelEndpointSetting?> GetEndpointAsync(string name, CancellationToken ct = default);

    Task UpsertEndpointAsync(ModelEndpointSetting endpoint, CancellationToken ct = default);

    Task DeleteEndpointAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<RoleBinding>> ListRolesAsync(CancellationToken ct = default);

    Task SetRoleAsync(string role, string endpointName, CancellationToken ct = default);

    /// <summary>写一条审计（谁、何时、对什么做了什么）——<b>不含内容</b>。</summary>
    Task AppendAuditAsync(string action, string actor, string? target, CancellationToken ct = default);

    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken ct = default);
}
