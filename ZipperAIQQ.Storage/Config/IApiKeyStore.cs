namespace ZipperAIQQ.Storage.Config;

/// <summary>
/// 一把留档的 API key（**密文**；明文永不入库）。
/// </summary>
/// <param name="Slot">
/// 槽位：<c>0</c> = **生效的那把**，其余是留档（问题记录 06 §5「保留多密钥写法」）。
/// </param>
/// <param name="KeyCipher">Data Protection 加密后的密文。</param>
public sealed record StoredApiKey(int Slot, string KeyCipher, DateTimeOffset UpdatedAt);

/// <summary>
/// API key 槽位仓储（问题记录 06 §5：对外一把 key、内部保留多密钥写法）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不复用 <c>ModelEndpoints.KeyCipher</c>：那张表的语义是「一个端点 = 地址 + 模型 + 它的 key」，
/// 而收敛后的 key 属于**账号级**（Chat / Search / OCR / ASR / TTS 共用一把），
/// 塞回端点表会变成「同一把 key 存三遍、改一处漏两处」。
/// </para>
/// <para>
/// 与端点表的关系：<c>primary</c> / <c>judge</c> / <c>search</c> 三行的旧密文**保留不删**，
/// 只作为迁移来源与人工恢复的留档，不再参与首选解析。
/// </para>
/// </remarks>
public interface IApiKeyStore
{
    /// <summary>按槽位升序列出全部留档 key（含密文）。</summary>
    Task<IReadOnlyList<StoredApiKey>> ListAsync(CancellationToken ct = default);

    /// <summary>取某个槽位；不存在返回 <c>null</c>。</summary>
    Task<StoredApiKey?> GetAsync(int slot, CancellationToken ct = default);

    /// <summary>写入或覆盖某个槽位（不碰其他槽位）。</summary>
    Task UpsertAsync(int slot, string keyCipher, CancellationToken ct = default);

    /// <summary>删除某个槽位（界面上没有入口；留给人工运维）。</summary>
    Task DeleteAsync(int slot, CancellationToken ct = default);
}
