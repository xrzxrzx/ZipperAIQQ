using ZipperAIQQ.Server.Config;

namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// 「全局 key 优先、legacy 端点密文回落」的唯一解析口径。
/// </summary>
/// <remarks>
/// <para>
/// 抽出来是因为这个顺序本身是**回归过的心跳**：密钥收敛（问题记录 06 §5）之后，
/// 只要有消费方偷偷只解端点密文，就会出现「界面说换了 key、实际还在用旧的」——
/// 2026-11-23 的独立审查正是在 <c>BalanceMonitor</c> 上抓到这条。
/// 顺序只有一处实现，才有一处测试。
/// </para>
/// <para>
/// 回落不是给新部署用的（那时根本没有 legacy 密文），是给「升级后还没填新 key」和
/// 「密钥环被换过」两种窘境留的活路。
/// </para>
/// </remarks>
public static class KeyFallback
{
    /// <summary>
    /// 取用于一次调用的 key：**全局槽位优先**，为空时才去要端点留档密文。
    /// </summary>
    /// <param name="keys">全局 key provider。</param>
    /// <param name="protector">解 legacy 密文用。</param>
    /// <param name="cipherProvider">
    /// **惰性**取端点留档密文（可空）。做成委托而不是字符串，是因为全局 key 存在时
    /// 根本不需要碰端点表：搜索是热路径，没必要每次先读一遍库；而且那次读一旦抛错，
    /// 本来能用的全局 key 也会一起拿不到。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可用的 key；两份都没有则 <c>null</c>。</returns>
    public static async Task<string?> ResolveAsync(
        IDeepSeekKeyProvider keys,
        ISecretProtector protector,
        Func<CancellationToken, Task<string?>> cipherProvider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(cipherProvider);

        var global = await keys.GetAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(global))
        {
            return global;
        }

        return protector.Unprotect(await cipherProvider(ct).ConfigureAwait(false));
    }
}
