namespace ZipperAIQQ.Server.Douyin;

/// <summary>
/// 一条作品下的评论读取结果（B5）。
/// </summary>
/// <param name="ItemId">作品 id。</param>
/// <param name="Comments">按视觉顺序（置顶在最前）排好的评论。</param>
/// <param name="Pages">实际翻了几页（底座侧的滚动次数）。</param>
/// <param name="Truncated">是否因为条数上限被截断。</param>
/// <param name="Note">底座给的一句补充（如「只回了前 N 条」）。</param>
public sealed record DouyinCommentList(
    string ItemId,
    IReadOnlyList<DouyinCommentItem> Comments,
    int Pages,
    bool Truncated,
    string? Note);
