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
public sealed class DouyinCommentService : ICommentReader, ICommentReplier
{
    /// <summary>写闸门里的动作名（进 <c>DouyinWrites.Action</c>）。</summary>
    public const string ReplyAction = "commentReply";

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

    /// <summary>回复正文的长度上限（字符）：抖音评论框塞不下长文，长了要**先拒**而不是让它被截断。</summary>
    /// <remarks>
    /// 截断一条对外发言是最糟的处理方式：说出去的话被砍一半，比"没发"更容易得罪人。
    /// </remarks>
    private const int MaxReplyChars = 300;

    private readonly IDouyinCommentSource _source;
    private readonly DouyinCommentOptions _options;
    private readonly IWriteGate _gate;
    private readonly IOwnerLookup? _owner;
    private readonly ILogger _logger;

    public DouyinCommentService(
        IDouyinCommentSource source,
        DouyinCommentOptions options,
        IWriteGate gate,
        ILogger logger,
        IOwnerLookup? owner = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _options = options;
        _gate = gate;
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

    // ─────────────────────────── 写：回一条评论（G3 闸门） ───────────────────────────

    /// <inheritdoc />
    public bool CanReply => CanRead && _options.WriteEnabled;

    /// <inheritdoc />
    public string ReplyUnavailableReason
    {
        get
        {
            if (!_options.Enabled)
            {
                return "读评论没开（douyin.commentEnabled=false）。";
            }

            if (!_source.IsReady)
            {
                return _source.NotReadyReason;
            }

            return _options.WriteEnabled
                ? string.Empty
                : "回评论没开（douyin.commentWriteEnabled=false）：写回复默认关闭，打开后才受日配额约束。";
        }
    }

    /// <inheritdoc />
    public async Task<CommentReplyOutcome> ReplyAsync(
        CommentReplyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.HasLocator)
        {
            return CommentReplyOutcome.Failed(
                "要指定回复哪一条：给评论 id，或者给 作者 + 内容片段（读评论时那些信息都有）。");
        }

        if (!CanReply)
        {
            return CommentReplyOutcome.Failed(ReplyUnavailableReason);
        }

        var text = (request.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return CommentReplyOutcome.Failed("回复正文是空的，没发。");
        }

        if (text.Length > MaxReplyChars)
        {
            // 截断一条**对外发言**是最糟的处理：话被砍一半比"没发"更容易得罪人
            return CommentReplyOutcome.Failed(
                $"回复太长（{text.Length} 字 > 上限 {MaxReplyChars} 字）：写短一点再发。");
        }

        var quota = new WriteQuota(
            Action: ReplyAction,
            PerDay: _options.ReplyPerDay,
            MinIntervalMs: Math.Max(0, _options.ReplyIntervalSeconds) * 1000,
            OncePerTarget: _options.ReplyOncePerTarget);

        var targetKey = TargetKeyOf(request);
        var now = DateTimeOffset.UtcNow;

        // 干跑**不占配额**：它没有任何对外动作。走闸门会让「先验一遍」把真回复的机会吃掉。
        if (!request.DryRun)
        {
            var decision = await _gate.CheckAsync(quota, targetKey, now, ct).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                // 拒绝也要留痕：主人事后翻账，"没人试过"与"有人试了被拦"必须能区分
                await _gate.RecordAsync(
                        quota, targetKey, request.ItemId, request.CommentId,
                        ok: false, note: $"denied:{decision.Reason}", now, ct)
                    .ConfigureAwait(false);

                return CommentReplyOutcome.Refused(decision.Reason ?? "写闸门拒绝了这次回复。");
            }
        }

        var timeoutMs = Math.Clamp(_options.TimeoutSeconds, 5, 180) * 1000;

        DouyinCommentReplyResult result;
        try
        {
            result = await _source
                .ReplyAsync(
                    new DouyinCommentReplyRequest(
                        ItemId: request.ItemId,
                        Text: text,
                        Commit: !request.DryRun,
                        CommentId: request.CommentId,
                        Author: request.Author,
                        TextMatch: request.TextMatch),
                    timeoutMs,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "回评论失败：itemId={ItemId} 目标={Target}", request.ItemId, targetKey);

            if (!request.DryRun)
            {
                await _gate.RecordAsync(
                        quota, targetKey, request.ItemId, request.CommentId,
                        ok: false, note: $"failed:{ex.Message}", DateTimeOffset.UtcNow, ct)
                    .ConfigureAwait(false);
            }

            return CommentReplyOutcome.Failed(ex.Message);
        }

        if (!request.DryRun)
        {
            var note = result.Readback ? "readback" : "submitted";
            await _gate.RecordAsync(
                    quota, targetKey, request.ItemId, request.CommentId,
                    ok: true, note: note, DateTimeOffset.UtcNow, ct)
                .ConfigureAwait(false);
        }

        _logger.Information(
            "回评论完成：itemId={ItemId} 目标={Target} 干跑={DryRun} 回读={Readback}",
            request.ItemId, targetKey, result.DryRun, result.Readback);

        return new CommentReplyOutcome(
            Ok: true,
            Error: null,
            Denied: false,
            DryRun: result.DryRun,
            MatchedBy: result.MatchedBy,
            Readback: result.Readback,
            Note: result.Note);
    }

    /// <summary>
    /// 配额键：优先**作者**（「同人一次」按人算），其次**评论 id**（同一条只回一次）。
    /// </summary>
    private static string TargetKeyOf(CommentReplyRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Author))
        {
            return $"a:{request.Author.Trim()}";
        }

        return !string.IsNullOrWhiteSpace(request.CommentId)
            ? $"c:{request.CommentId.Trim()}"
            : "unknown";
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
