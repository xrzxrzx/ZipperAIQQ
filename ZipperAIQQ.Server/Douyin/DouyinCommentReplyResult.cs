namespace ZipperAIQQ.Server.Douyin;

/// <summary>
/// 一次回评论的结果（B5）。
/// </summary>
/// <param name="ItemId">作品 id。</param>
/// <param name="DryRun">是否干跑（<c>true</c> = 只填入并校验，**没有提交**）。</param>
/// <param name="MatchedBy">靠什么定位到的（<c>id</c> / <c>text</c> / <c>author+text</c>）。</param>
/// <param name="Readback">提交后是否在页面上回读到自己那条。</param>
/// <param name="Note">底座给的补充说明（如「已提交但没回读到」）。</param>
/// <remarks>
/// <para>
/// <c>Readback=false</c> **不等于失败**：提交动作成功了，只是页面还没刷出来。
/// 但也不能说成「对方一定看到了」—— 所以它是独立字段，让上层照实转述。
/// </para>
/// <para>
/// <c>MatchedBy</c> 要带出来：靠 <c>author+text</c> 命中的回复，比靠 <c>id</c> 命中的更容易挑错人，
/// 主人事后核账时这个信息有用。
/// </para>
/// </remarks>
public sealed record DouyinCommentReplyResult(
    string ItemId,
    bool DryRun,
    string? MatchedBy,
    bool Readback,
    string? Note);
