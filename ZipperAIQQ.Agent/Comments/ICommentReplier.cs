namespace ZipperAIQQ.Agent.Comments;

/// <summary>
/// 回一条抖音评论（B5）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ICommentReader"/> 分开：读是「看一眼」，写是**代替主人对外发言**。
/// 写能力默认关（<c>douyin.commentWriteEnabled=false</c>），打开后还要过写闸门
/// （日配额 / 最小间隔 / 同人一次）—— 两道关卡的失效方式不同，缺一不可。
/// </para>
/// <para>
/// <see cref="CommentReplyRequest.DryRun"/> 是「只填入不发」：会真的点开回复、写入并校验
/// <c>@</c> 前缀，但**不提交**，对外零副作用。它不占配额（没有对外动作）。
/// </para>
/// </remarks>
public interface ICommentReplier
{
    /// <summary>能不能写（底座已连 + 读开关 + **写开关**）。</summary>
    bool CanReply { get; }

    /// <summary>不能写的人话原因；可用时为空串。</summary>
    string ReplyUnavailableReason { get; }

    /// <summary>回一条评论。**不抛业务失败**：拒绝 / 失败都走 <see cref="CommentReplyOutcome"/>。</summary>
    /// <param name="request">定位条件 + 正文 + 是否干跑。</param>
    /// <param name="ct">取消令牌。</param>
    Task<CommentReplyOutcome> ReplyAsync(
        CommentReplyRequest request, CancellationToken ct = default);
}
