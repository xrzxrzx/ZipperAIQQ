namespace ZipperAIQQ.Agent.Comments;

/// <summary>
/// 评论区里的一条评论（给模型看的形状，B5）。
/// </summary>
/// <param name="Id">评论 id；页面里取不到时为 <c>null</c>。</param>
/// <param name="Author">作者昵称。</param>
/// <param name="AuthorId">作者 uid（可能有）。</param>
/// <param name="Text">正文（已按上限截断，截断时 <c>TextTruncated</c> 为 true）。</param>
/// <param name="Time">相对时间原文（如 <c>2小时前</c>）。</param>
/// <param name="Likes">点赞数。</param>
/// <param name="ReplyCount">楼中楼条数。</param>
/// <param name="Pinned">是否置顶。</param>
/// <param name="MentionsOwner">
/// 这条评论是否 <b>@ 了主人</b>（= 本号）。<c>null</c> = 判断不了（主人昵称没配），不是"没有"。
/// </param>
/// <param name="Mentions">正文里出现的 <c>@某某</c>。</param>
/// <param name="TextTruncated">正文是否被截断过。</param>
public sealed record CommentItem(
    string? Id,
    string Author,
    string? AuthorId,
    string Text,
    string? Time,
    int? Likes,
    int? ReplyCount,
    bool Pinned,
    bool? MentionsOwner,
    IReadOnlyList<string> Mentions,
    bool TextTruncated);
