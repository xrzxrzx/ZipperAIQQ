namespace ZipperAIQQ.Storage.Express;

/// <summary>
/// 一份档位快照（表达档位设计 §7.2，schema v22 表 <c>ExpressSnapshots</c>）。
/// </summary>
/// <param name="Id">主键（回滚时用它指定要回到哪一份）。</param>
/// <param name="AtMs">应用前时间（UTC ms）—— 快照记的是**改之前**的样子。</param>
/// <param name="Actor">改动的操作者（<c>http-client</c> / <c>startup</c> …）。</param>
/// <param name="PayloadJson">档位全文（与运行期 <c>express</c> 键同一份序列化）。</param>
/// <param name="Checksum">SHA-256(payload)。回滚前比对：不一致说明库被手改过，宁可拒绝。</param>
/// <remarks>
/// 只存配置、**不存对话内容**：这是「配置回滚点」，不是聊天备份。
/// </remarks>
public sealed record ExpressSnapshot(
    long Id,
    long AtMs,
    string Actor,
    string PayloadJson,
    string Checksum);
