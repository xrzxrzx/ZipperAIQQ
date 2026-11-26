namespace ZipperAIQQ.Storage.Douyin;

/// <summary>
/// 一次抖音写动作的留痕（表 <c>DouyinWrites</c>，schema v21）。
/// </summary>
/// <remarks>
/// <para>
/// 这张表同时干两件事：**配额计数**（按 <paramref name="Action"/> + 时间窗）与**留痕**
/// （谁在什么时候对哪条评论写了什么结果）。做成一次写入而不是两处记账 ——
/// 分开写迟早出现"配额扣了但审计没记"或反过来。
/// </para>
/// <para>
/// <b>不存正文</b>：写出去的话属于对外内容，不属于运维审计；这里只留
/// 「对哪一条（<paramref name="ItemId"/> / <paramref name="CommentId"/>）+ 成没成 + 一句备注」。
/// </para>
/// </remarks>
/// <param name="Action">动作（如 <c>commentReply</c>）。</param>
/// <param name="TargetKey">配额键（同人一次用作者 id，否则用评论 id）。</param>
/// <param name="Ok">这次动作成没成（被闸门拒绝也算一条，<c>false</c>）。</param>
/// <param name="At">动作时间（UTC）。</param>
/// <param name="Id">自增主键（写入时给 <c>0</c>）。</param>
/// <param name="ItemId">作品 id。</param>
/// <param name="CommentId">评论 id。</param>
/// <param name="Note">一句话结果（<c>denied:</c> / <c>failed:</c> / <c>readback</c> …）。</param>
public sealed record DouyinWriteAction(
    string Action,
    string TargetKey,
    bool Ok,
    DateTimeOffset At,
    long Id = 0,
    string? ItemId = null,
    string? CommentId = null,
    string? Note = null);
