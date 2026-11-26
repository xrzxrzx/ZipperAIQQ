using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Comments;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Server.Douyin;

/// <summary>
/// 抖音评论区的实现（B5）：底座读评论 → 归一化 + 补「是否 @ 了主人」→ 交给 Agent。
/// </summary>
/// <remarks>
/// <para>
/// 分工：<see cref="IDouyinCommentSource"/> 负责「跟浏览器要东西」（页面导航 / 滚动 / DOM），
/// 本类负责「业务口径」—— 条数与页数上限、正文截断、<c>@主人</c> 判定、把缺失说成缺失。
/// </para>
/// <para>
/// **<c>@主人</c> 判定放在服务端**：它要知道「主人这个平台叫什么」，而主人配置在
/// <c>AccessSettings</c> 里（通过 <see cref="IOwnerLookup"/> 拿）。底座只管把 <c>@某某</c>
/// 原样抄回来，不猜谁是主人。
/// </para>
/// <para>
/// <b>判定不了就说判定不了</b>：主人昵称没配时 <c>MentionsOwner</c> 一律 <c>null</c>
/// （不是 <c>false</c>）—— <c>false</c> 是"确认没 @ 我"，<c>null</c> 是"我不知道谁是主人"，
/// 这两件事对模型的行为完全不同（前者可以放心略过，后者应该提醒主人去配）。
/// </para>
/// </remarks>
public sealed class DouyinCommentService : ICommentReader
{
    /// <summary>一次最多翻几页（与配置取小）。</summary>
    private const int HardMaxPages = 10;

    /// <summary>一次最多回几条（与配置取小）。</summary>
    private const int HardMaxComments = 100;

    /// <summary>单条评论进上下文的字符上限（超出截断并标注）。</summary>
    /// <remarks>
    /// 评论区里有人写长文（复制粘贴整段话）。一条 5000 字的评论就能把整轮上下文吃干，
    /// 而"大家在说什么"这个用途根本不需要全文。
    /// </remarks>
    private const int MaxCommentChars = 500;

    private readonly IDouyinCommentSource _source;
    private readonly DouyinCommentOptions _options;
    private readonly IOwnerLookup? _owner;
    private readonly ILogger _logger;

    public DouyinCommentService(
        IDouyinCommentSource source,
        DouyinCommentOptions options,
        ILogger logger,
        IOwnerLookup? owner = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _options = options;
        _logger = logger;
        _owner = owner;
    }

    /// <inheritdoc />
    public bool CanRead => _options.Enabled && _source.IsReady;

    /// <inheritdoc />
    public string UnavailableReason
    {
        get
        {
            if (!_options.Enabled)
            {
                return "读评论没开（douyin.commentEnabled=false）。";
            }

            return _source.IsReady ? string.Empty : _source.NotReadyReason;
        }
    }

    /// <inheritdoc />
    public async Task<CommentPage> ListAsync(
        string itemId, int pages, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

        if (!CanRead)
        {
            throw new InvalidOperationException(UnavailableReason);
        }

        var maxPages = Math.Clamp(pages > 0 ? pages : _options.MaxPages, 1, HardMaxPages);
        var maxComments = Math.Clamp(limit > 0 ? limit : _options.MaxComments, 1, HardMaxComments);
        var timeoutMs = Math.Clamp(_options.TimeoutSeconds, 5, 180) * 1000;

        var list = await _source
            .ListAsync(itemId, maxPages, maxComments, timeoutMs, ct)
            .ConfigureAwait(false);

        var (_, ownerName) = await ResolveOwnerAsync(ct).ConfigureAwait(false);

        // 能不能判「@主人」只看**昵称**：评论区里的提及是纯文本 `@某某`，
        // 只有名字能拿来比；只配了 uid 的部署照样判不出来（那就如实说判不出来）
        var canJudgeMentions = !string.IsNullOrWhiteSpace(ownerName);

        var comments = list.Comments
            .Select(comment => Map(comment, ownerName, canJudgeMentions))
            .ToList();

        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(list.Note))
        {
            notes.Add(list.Note);
        }

        if (list.Truncated)
        {
            notes.Add($"只回了前 {maxComments} 条（评论区还有更多）");
        }

        if (comments.Count == 0)
        {
            notes.Add("这条作品下没读到评论（也可能是评论区被关闭 / 加载失败）");
        }

        if (!canJudgeMentions)
        {
            notes.Add("没配抖音主人昵称，`@主人` 判断不可用（在「人员与名单」里配主人才判得出）");
        }

        return new CommentPage(
            ItemId: list.ItemId,
            Comments: comments,
            Pages: list.Pages,
            Truncated: list.Truncated || comments.Count >= maxComments,
            OwnerUnknown: !canJudgeMentions,
            Note: notes.Count == 0 ? null : string.Join('；', notes));
    }

    /// <summary>把底座的一条评论映射成给模型看的形状（截断 + <c>@主人</c> 判定）。</summary>
    private static CommentItem Map(
        DouyinCommentItem comment, string? ownerName, bool canJudgeMentions)
    {
        var text = comment.Text ?? string.Empty;
        var truncated = text.Length > MaxCommentChars;
        if (truncated)
        {
            text = string.Concat(text.AsSpan(0, MaxCommentChars), "…");
        }

        // 判不了就给 null（= 不知道），不要给 false（= 确认没 @ 我）：两者会导致完全不同的行为
        bool? mentionsOwner = canJudgeMentions
            ? comment.Mentions.Any(mention =>
                mention.Trim().Contains(ownerName!.Trim(), StringComparison.Ordinal))
            : null;

        return new CommentItem(
            Id: comment.Id,
            Author: comment.Author,
            AuthorId: comment.AuthorId,
            Text: text,
            Time: comment.Time,
            Likes: comment.Likes,
            ReplyCount: comment.ReplyCount,
            Pinned: comment.Pinned,
            MentionsOwner: mentionsOwner,
            Mentions: comment.Mentions,
            TextTruncated: truncated);
    }

    /// <summary>
    /// 取抖音侧的主人（昵称用于 <c>@</c> 判定）。查不到就返回空对 —— **不抛**：
    /// 读评论不该因为"主人没配"整个失败，只是 <c>@主人</c> 这一列判不出来。
    /// </summary>
    private async Task<(string? OwnerId, string? OwnerName)> ResolveOwnerAsync(CancellationToken ct)
    {
        if (_owner is null)
        {
            return (null, null);
        }

        try
        {
            return await _owner.GetAsync(DouyinChannel.PlatformName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "查抖音主人失败：这次读评论的 `@主人` 判定不可用");
            return (null, null);
        }
    }
}
