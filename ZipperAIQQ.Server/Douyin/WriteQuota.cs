namespace ZipperAIQQ.Server.Douyin;

/// <summary>
/// 一种写动作的配额（问题记录 06 · G3「写闸门」）。
/// </summary>
/// <param name="Action">动作名（进 <c>DouyinWrites.Action</c>，也是配额的分组键）。</param>
/// <param name="PerDay">24 小时滚动窗口内最多几次（<c>&lt;= 0</c> 等于关闭）。</param>
/// <param name="MinIntervalMs">两次之间至少隔多久（毫秒，<c>0</c> = 不限制）。</param>
/// <param name="OncePerTarget">同一目标（作者 / 评论）在窗口内只准写一次。</param>
/// <remarks>
/// 默认值来自 <c>douyin.commentReplyPerDay</c> / <c>commentReplyIntervalSeconds</c> /
/// <c>commentReplyOncePerTarget</c>（方案 G3：回复 10/天、间隔 30 秒、同人一次）。
/// 顶层评论的配额（20/天）留着给「主动发评论」那条能力用，B5 只实现 @ 回复。
/// </remarks>
public sealed record WriteQuota(string Action, int PerDay, int MinIntervalMs, bool OncePerTarget);
