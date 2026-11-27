namespace ZipperAIQQ.Server.Express;

/// <summary>
/// 快照保留策略（表达档位设计 §7.2）——**改档与回滚共用同一个数**。
/// </summary>
/// <remarks>
/// <para>
/// 为什么抽成共享常量而不是各写各的：回滚也写快照（回滚错了要能再退回来），
/// 若只有改档那条路径做 Trim，连着回滚几轮表就超过「保留最近 N 份」的承诺并且无上界增长。
/// </para>
/// <para>
/// 固定值、不另做配置键：回滚点是运维兜底，多一个旋钮就多一处可能与代码漂移的真相；
/// 真要更多，改这一行比改配置更快。
/// </para>
/// </remarks>
public static class ExpressSnapshotPolicy
{
    /// <summary>保留最近几份。</summary>
    public const int Keep = 5;
}
