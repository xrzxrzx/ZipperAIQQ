namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// 全局 key 的**对外状态**（只给界面看，不含明文）。
/// </summary>
/// <param name="Configured">是否有一把可用的 key。</param>
/// <param name="Slots">留档槽位数（&gt;1 说明还有不生效的那几把）。</param>
/// <param name="Masked">生效 key 的掩码（<c>sk-****a1b2</c>）；没有 key 时为空串。</param>
public sealed record DeepSeekKeyStatus(bool Configured, int Slots, string Masked);
