namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 抖音评论区（B5）的运行参数（<c>douyin</c> 配置节的 <c>comment*</c> 键）。
/// </summary>
/// <remarks>
/// <para>
/// 键名**摊平成两级**（<c>douyin.commentMaxPages</c>，不是 <c>douyin.comment.maxPages</c>）：
/// 加载器只支持「section → key」两级（同 B2-b / B3 / B4 的偏差记录）。
/// </para>
/// <para>
/// 这里是**唯一真相**：分几页、读几条、写配额都在服务端算，sidecar 只兜硬上限
/// （一次最多 10 页 / 100 条 / 90 秒）。
/// </para>
/// <para>
/// **写能力默认关**（方案 D-08 / Q-05）：读评论是只读动作，写回复是**代替主人对外发言** ——
/// 前者开箱可用，后者必须显式打开，且打开后还受 <c>WriteGate</c> 的日配额与间隔约束。
/// </para>
/// </remarks>
public sealed record DouyinCommentOptions
{
    /// <summary>
    /// 是否把 <c>douyin_comments</c>（读评论）给模型（默认开）。
    /// </summary>
    /// <remarks>
    /// 关的是「工具存不存在」：底座连不上时工具本来就不会出现（<c>IsReady=false</c>）；
    /// 这个开关是给「底座能用但不想让它去翻评论区」的场合。
    /// </remarks>
    public bool Enabled { get; init; } = true;

    /// <summary>一次最多翻几页评论（默认 3，硬顶 10）。</summary>
    /// <remarks>
    /// 「页」= 一次向下滚动拿到的增量；抖音评论是懒加载 + 虚拟滚动，页数太大会把
    /// 共享的那个 Chromium 标签页顶到 OOM，而评论区本身也不值得读那么深。
    /// </remarks>
    public int MaxPages { get; init; } = 3;

    /// <summary>一次最多回几条评论（默认 30，硬顶 100）。</summary>
    /// <remarks>每条评论都要进上下文；30 条已经够回答「大家在说什么」了。</remarks>
    public int MaxComments { get; init; } = 30;

    /// <summary>单次评论请求超时（秒，默认 60）。</summary>
    /// <remarks>要导航到作品页 + 等懒加载 + 滚动分页，比普通查询慢得多，不能共用 <c>douyin.queryTimeoutMs</c>。</remarks>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// 是否允许 <c>douyin_comment_reply</c>（默认**关**）。
    /// </summary>
    /// <remarks>
    /// 打开之后还要过 <c>WriteGate</c>（日配额 / 同人一次 / 间隔）：这个开关管的是
    /// 「这个能力存不存在」，闸门管的是「这一次准不准」。两道都要，因为它们的失效方式不同 ——
    /// 开关忘了关是"能力暴露"，闸门忘了配是"一把梭"。
    /// </remarks>
    public bool WriteEnabled { get; init; }

    /// <summary>普通评论（对视频发顶层评论）的日配额（默认 20）。</summary>
    public int CommentPerDay { get; init; } = 20;

    /// <summary>@ 回复的日配额（默认 10）。</summary>
    /// <remarks>回复比顶层评论更容易被判定为「机器人」，配额砍半（方案 G3）。</remarks>
    public int ReplyPerDay { get; init; } = 10;

    /// <summary>两次回复之间的最小间隔（秒，默认 30）。</summary>
    public int ReplyIntervalSeconds { get; init; } = 30;

    /// <summary>同一个人只回一次（默认开）。</summary>
    /// <remarks>按「作者 id」计数；拿不到作者 id 时退化成「同一条评论只回一次」。</remarks>
    public bool ReplyOncePerTarget { get; init; } = true;
}
