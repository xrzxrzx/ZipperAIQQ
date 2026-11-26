namespace ZipperAIQQ.Server.Douyin;

/// <summary>
/// 写闸门的窄接口（G3）：问「这次准不准」，再把结果记下来。
/// </summary>
/// <remarks>
/// <para>
/// 方案 G3 原本只有一个 <c>AcquireAsync</c>（问一句就扣配额）。实现时拆成
/// <see cref="CheckAsync"/> + <see cref="RecordAsync"/> 两步，理由有两条：
/// </para>
/// <para>
/// ① <b>「扣配额」和「写成了」不是一回事</b>：点「回复」之后可能被风控拦下、可能输入框丢
/// <c>@</c> 前缀、可能超时。先扣再写会让失败也吃掉配额，而当天配额只有 10 次；
/// ② <b>拒绝也要留痕</b>：被闸门拒的那一次同样要落一条 <c>Ok=false</c> 的记录，
/// 否则主人事后翻账只看到"成功的 3 条"，看不出"还有 7 次被拦"。
/// </para>
/// <para>
/// 代价是两步之间有窗口（两个并发请求可能都通过检查）。评论回复是**单账号、串行、
/// 分钟级**的动作，这个窗口没有实际意义；真需要强一致时在 <see cref="WriteGate"/> 里加锁即可。
/// </para>
/// </remarks>
public interface IWriteGate
{
    /// <summary>问：这一次准不准（**只读**，不扣配额）。</summary>
    /// <param name="quota">这条动作的配额。</param>
    /// <param name="targetKey">配额键（同人一次用作者 id，否则用评论 id）。</param>
    /// <param name="now">当前时间（注入以便单测）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<WriteDecision> CheckAsync(
        WriteQuota quota, string targetKey, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>记：这一次的结果（成功、失败、被拒**都要记**）。</summary>
    /// <param name="quota">这条动作的配额（只取 <see cref="WriteQuota.Action"/>）。</param>
    /// <param name="targetKey">配额键。</param>
    /// <param name="itemId">作品 id。</param>
    /// <param name="commentId">评论 id。</param>
    /// <param name="ok">成没成（被闸门拒绝也记 <c>false</c>）。</param>
    /// <param name="note">一句话结果（<c>denied:</c> / <c>failed:</c> / <c>readback</c> …）。</param>
    /// <param name="now">当前时间（注入以便单测）。</param>
    /// <param name="ct">取消令牌。</param>
    Task RecordAsync(
        WriteQuota quota,
        string targetKey,
        string? itemId,
        string? commentId,
        bool ok,
        string? note,
        DateTimeOffset now,
        CancellationToken ct = default);
}
