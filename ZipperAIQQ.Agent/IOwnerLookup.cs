namespace ZipperAIQQ.Agent;

/// <summary>
/// 按平台查「主人是谁」（id + 昵称）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么抽接口：Agent 层需要知道"当前这个人是不是主人、主人叫什么"，但**主人的真值**
/// 在 Server 的 <c>AccessSettings</c> 里（每平台一套 <c>access.{platform}.owner</c>，含旧键回退）。
/// Agent 不该去读 Server 的键名，否则键格式一改两处一起坏。
/// </para>
/// <para>
/// 由 Server 实现（复用 <c>AccessSettings</c>），Agent 只依赖这个窄接口。
/// </para>
/// </remarks>
public interface IOwnerLookup
{
    /// <summary>取某平台的主人；未配置时两个值都是 <c>null</c>。</summary>
    Task<(string? OwnerId, string? OwnerName)> GetAsync(string platform, CancellationToken ct = default);
}
