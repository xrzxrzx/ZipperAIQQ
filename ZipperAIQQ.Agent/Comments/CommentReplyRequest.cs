namespace ZipperAIQQ.Agent.Comments;

/// <summary>
/// 回一条抖音评论（B5）。
/// </summary>
/// <remarks>
/// 与读共用「定位三条件」：评论 id 最稳；没有 id 时用 作者 + 内容片段。
/// <see cref="DryRun"/> 是给模型与验收用的「只填入不发」：它会真的点开回复、输入并校验
/// <c>@</c> 前缀，但**不提交**，所以对外零副作用。
/// </remarks>
/// <param name="ItemId">作品 id。</param>
/// <param name="Text">要回复的正文。</param>
/// <param name="CommentId">评论 id（可空）。</param>
/// <param name="Author">评论作者昵称（可空）。</param>
/// <param name="TextMatch">评论内容片段（可空）。</param>
/// <param name="DryRun">只填入不提交。</param>
public sealed record CommentReplyRequest(
    string ItemId,
    string Text,
    string? CommentId = null,
    string? Author = null,
    string? TextMatch = null,
    bool DryRun = false)
{
    /// <summary>三个定位条件是不是至少给了一个（给不出就该**在动手前**拒绝，而不是让底座猜）。</summary>
    public bool HasLocator =>
        !string.IsNullOrWhiteSpace(CommentId)
        || !string.IsNullOrWhiteSpace(Author)
        || !string.IsNullOrWhiteSpace(TextMatch);
}
