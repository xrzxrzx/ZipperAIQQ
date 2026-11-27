namespace ZipperAIQQ.Agent.Express;

/// <summary>
/// 生效档解析（表达档位设计 §2.1）：纯函数，便于把优先级矩阵钉死在单测里。
/// </summary>
/// <remarks>
/// <para>
/// 优先级：**会话覆盖 &gt; 群上限 &gt; 全局 &gt; daily**。
/// </para>
/// <para>
/// ⚠️ 群聊时上限必须是**真上限**：<c>min(会话覆盖 ?? 全局, groupCap)</c>。
/// 若会话覆盖能绕过 <c>groupCap</c>，那「<c>express.groupCap=daily</c> 一句话收紧群里尺度」
/// （设计 §12 X-1 的缓解措施）就是句假话 —— 任何一个被单独设过的群都能绕过它。
/// 取 <c>min</c> 同时满足两个诉求：会话覆盖仍优先于全局，群上限仍只收紧不放宽。
/// </para>
/// </remarks>
public static class ExpressLevelPolicy
{
    /// <summary>算本轮生效档。</summary>
    /// <param name="settings">档位快照。</param>
    /// <param name="sessionId">完整会话 ID（会话覆盖的键）；空则跳过覆盖。</param>
    /// <param name="isGroup">当前会话是不是群聊（决定群上限是否参与）。</param>
    public static ExpressLevel Resolve(ExpressSettings settings, string? sessionId, bool isGroup)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // ① 会话覆盖（没有就退回全局默认）
        var level = settings.Level;
        if (!string.IsNullOrWhiteSpace(sessionId) &&
            settings.SessionOverrides.TryGetValue(sessionId, out var overridden))
        {
            level = overridden;
        }

        // ② 群上限只收紧不放宽（min）
        if (isGroup)
        {
            level = Lower(level, settings.GroupCap);
        }

        return level;
    }

    /// <summary>取两个档位中更低的一档。</summary>
    public static ExpressLevel Lower(ExpressLevel a, ExpressLevel b) => a <= b ? a : b;
}
