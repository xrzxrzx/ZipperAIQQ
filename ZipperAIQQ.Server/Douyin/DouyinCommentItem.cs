namespace ZipperAIQQ.Server.Douyin;

/// <summary>
/// 评论区里的一条评论（底座回给服务端的形状，B5）。
/// </summary>
/// <param name="Id">评论 id；DOM 里取不到时为 <c>null</c>（只能用 作者 + 内容片段 定位）。</param>
/// <param name="Author">作者昵称（取不到时是占位串，不是编造的名字）。</param>
/// <param name="AuthorId">作者 uid（来自作者主页链接）。</param>
/// <param name="Text">正文（已去掉作者 / 时间 / 点赞 / 按钮文案）。</param>
/// <param name="Time">相对时间原文（如 <c>2小时前</c>）。</param>
/// <param name="Likes">点赞数（<c>1.2万</c> 已折算成 12000）。</param>
/// <param name="ReplyCount">「展开 N 条回复」里的 N。</param>
/// <param name="Pinned">是否置顶。</param>
/// <param name="Mentions">正文里出现的 <c>@某某</c>（去重、去尾部标点）。</param>
/// <remarks>
/// 这些字段**全部来自页面文本解析**，没有一个是平台结构化接口给的 ——
/// 所以「拿不到就给 <c>null</c>」，绝不用默认值把缺失伪装成事实。
/// </remarks>
public sealed record DouyinCommentItem(
    string? Id,
    string Author,
    string? AuthorId,
    string Text,
    string? Time,
    int? Likes,
    int? ReplyCount,
    bool Pinned,
    IReadOnlyList<string> Mentions);
