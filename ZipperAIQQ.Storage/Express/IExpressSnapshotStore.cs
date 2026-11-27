namespace ZipperAIQQ.Storage.Express;

/// <summary>
/// 档位快照仓储（表达档位设计 §7.2）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要有快照：档位 / 规则段是**手可改的提示词级配置**，改坏的直接表现是「她说话不对劲」，
/// 而那时最想要的是「退回到刚才那版」。没有快照就只能凭记忆重写。
/// </para>
/// <para>
/// 保留策略由上层传 <c>keep</c>（<c>express.snapshot.keep</c>）：仓储不内置默认值，
/// 免得配置与代码各有一份真相。
/// </para>
/// </remarks>
public interface IExpressSnapshotStore
{
    /// <summary>写一份快照（<b>改动之前</b>调），返回新行主键。</summary>
    Task<long> AppendAsync(
        long atMs, string actor, string payloadJson, string checksum, CancellationToken ct = default);

    /// <summary>按主键取快照；不存在返回 <c>null</c>。</summary>
    Task<ExpressSnapshot?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>最近的若干份（新的在前），供客户端列回滚点。</summary>
    Task<IReadOnlyList<ExpressSnapshot>> ListRecentAsync(int limit, CancellationToken ct = default);

    /// <summary>只保留最近 <paramref name="keep"/> 份，返回删除的行数。</summary>
    Task<int> TrimAsync(int keep, CancellationToken ct = default);
}
