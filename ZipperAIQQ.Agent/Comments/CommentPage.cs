namespace ZipperAIQQ.Agent.Comments;

/// <summary>
/// 一次读评论的结果（B5）。
/// </summary>
/// <param name="ItemId">作品 id。</param>
/// <param name="Comments">评论列表（置顶在最前，其余按页面顺序）。</param>
/// <param name="Pages">实际翻了几页。</param>
/// <param name="Truncated">是否因为条数上限被截断。</param>
/// <param name="OwnerUnknown">主人昵称没配，<c>@主人</c> 判定不可用（<c>MentionsOwner</c> 全是 <c>null</c>）。</param>
/// <param name="Note">补充说明（给模型照实转述）。</param>
public sealed record CommentPage(
    string ItemId,
    IReadOnlyList<CommentItem> Comments,
    int Pages,
    bool Truncated,
    bool OwnerUnknown,
    string? Note);
