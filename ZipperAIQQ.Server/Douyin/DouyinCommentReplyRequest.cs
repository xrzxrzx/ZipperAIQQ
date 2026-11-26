using System.Text.Json.Nodes;

namespace ZipperAIQQ.Server.Douyin;

/// <summary>
/// 一次回评论的请求（**定位三条件**：id 最稳，其次 作者 + 内容片段）。
/// </summary>
/// <param name="ItemId">作品 id。</param>
/// <param name="Text">要回复的正文（模型给的原文）。</param>
/// <param name="Commit">是否真的提交；<c>false</c> = 干跑（只填入并校验，不对外产生任何东西）。</param>
/// <param name="CommentId">评论 id（读评论时拿到的）。</param>
/// <param name="Author">评论作者昵称（没有 id 时的定位线索）。</param>
/// <param name="TextMatch">评论内容片段（没有 id 时的定位线索）。</param>
public sealed record DouyinCommentReplyRequest(
    string ItemId,
    string Text,
    bool Commit,
    string? CommentId = null,
    string? Author = null,
    string? TextMatch = null)
{
    /// <summary>拼成 <c>comment.reply</c> 帧的 payload（纯函数，可单测）。</summary>
    /// <remarks>
    /// 空字段**不下发**：sidecar 那边「字段存在但是空串」与「没这个字段」行为不同
    /// （空串会参与匹配，把定位条件变成一个永远匹配不上的谓词）。
    /// </remarks>
    public JsonObject ToPayload()
    {
        var payload = new JsonObject
        {
            ["itemId"] = ItemId,
            ["text"] = Text,
            ["commit"] = Commit,
        };

        if (!string.IsNullOrWhiteSpace(CommentId))
        {
            payload["commentId"] = CommentId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(Author))
        {
            payload["author"] = Author.Trim();
        }

        if (!string.IsNullOrWhiteSpace(TextMatch))
        {
            payload["textMatch"] = TextMatch.Trim();
        }

        return payload;
    }
}
