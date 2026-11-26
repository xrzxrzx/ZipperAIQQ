namespace ZipperAIQQ.Server.Douyin;

/// <summary>
/// 服务端 → 浏览器底座「读评论 / 回评论」的窄接口（B5）。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>DouyinChannel</c> 实现（连接、requestId 配对、超时都在它手里），
/// 消费者是 <see cref="CommentReadService"/> 与单元测试的假实现 ——
/// 帧名与 payload 形状不外泄给 Agent（与 <c>IDouyinMediaSource</c> 同一条依赖方向）。
/// </para>
/// <para>
/// 失败一律抛异常（带人话原因），**不返回空列表冒充「这条视频没有评论」**：
/// 「没有评论」与「没读到」对模型是两件事，前者可以让它换个视频，后者要它重试或如实说失败。
/// </para>
/// </remarks>
public interface IDouyinCommentSource
{
    /// <summary>侧车是否连着（没连上就什么都读不到）。</summary>
    bool IsReady { get; }

    /// <summary>不可用的人话原因；可用时为空串。</summary>
    string NotReadyReason { get; }

    /// <summary>读一条作品下的评论（分页由底座侧滚动完成）。</summary>
    /// <param name="itemId">作品 id（纯数字）。</param>
    /// <param name="maxPages">最多翻几页。</param>
    /// <param name="maxComments">最多回几条。</param>
    /// <param name="timeoutMs">超时（毫秒）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<DouyinCommentList> ListAsync(
        string itemId,
        int maxPages,
        int maxComments,
        int timeoutMs,
        CancellationToken ct = default);

    /// <summary>回一条评论（<c>Commit=false</c> 时只填入并校验，不提交）。</summary>
    /// <param name="request">定位条件 + 正文 + 是否提交。</param>
    /// <param name="timeoutMs">超时（毫秒）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<DouyinCommentReplyResult> ReplyAsync(
        DouyinCommentReplyRequest request,
        int timeoutMs,
        CancellationToken ct = default);
}
