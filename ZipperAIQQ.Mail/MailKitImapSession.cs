using System.Net;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Serilog;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Mail;

/// <summary>
/// <see cref="IImapSession"/> 的 MailKit 实现（**每账号一个**，内部串行）。
/// </summary>
/// <remarks>
/// <para>
/// 三家差异只有两处：① 网易系（163 / 126 / yeah.net / 188）连接后、认证前**必须**发 IMAP <c>ID</c> 命令，
/// 否则服务端回 <c>Unsafe Login</c>；② 其余（QQ / Outlook）按普通
/// <c>LOGIN</c> + SSL 走。ID 发送失败只记 Warning 不中断（设计 §5.1 / §10 风险 2）。
/// </para>
/// <para>
/// <c>ID</c> 命令会向服务端暴露客户端信息（RFC 2971），所以这里**只填通用的产品名与版本**，
/// 不填机器名 / 用户名 / 唯一标识 —— 那些能把用户从服务端日志里指认出来。
/// </para>
/// <para>
/// 正文优先取 <c>text/plain</c>（<see cref="MessageSummary.TextBody"/>）；只有 HTML 时走
/// <see cref="HtmlToText"/> 做简易剥离（**不新增包依赖**，见设计 §10 风险 4）。
/// 不整封下载 <c>MimeMessage</c>：附件会跟着一起下，几十兆的月报会把轮询拖死。
/// </para>
/// </remarks>
public sealed class MailKitImapSession : IImapSession
{
    /// <summary>收信文件夹（本期只收 INBOX，设计 §5.2）。</summary>
    private const string InboxFolder = "INBOX";

    /// <summary>必须在认证前发 <c>ID</c> 的邮箱域名（网易系）。</summary>
    private static readonly string[] IdentifyRequiredDomains =
    [
        "163.com",
        "126.com",
        "yeah.net",
        "188.com",
    ];

    private static readonly Regex ScriptStyleRegex = new(
        @"<(script|style)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CommentRegex = new(
        @"<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LineBreakTagRegex = new(
        @"<\s*(br|/p|/div|/tr|/li|/h[1-6]|/blockquote)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(@"<[^>]*>", RegexOptions.Compiled);

    private static readonly Regex HorizontalSpaceRegex = new(@"[ \t\f\v]+", RegexOptions.Compiled);

    private static readonly Regex BlankLinesRegex = new(@"(\r?\n){3,}", RegexOptions.Compiled);

    private readonly ImapClient _client = new();
    private readonly ILogger? _logger;
    private IMailFolder? _folder;

    /// <summary>构造一个未连接的会话。</summary>
    /// <param name="logger">
    /// 可空：<see cref="MailKitImapSessionFactory"/> 的无参构造下没有容器，日志就退化成静默；
    /// 由容器注入时（工厂的 ILogger 构造重载）才有 ID / 断开失败等诊断信息。
    /// </param>
    public MailKitImapSession(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(MailAccount account, string credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);

        if (_client.IsConnected)
        {
            throw new InvalidOperationException("会话已连接，请新建一个会话（连接不共享）。");
        }

        var socketOptions = account.UseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.None;

        await _client
            .ConnectAsync(account.ImapHost, account.ImapPort, socketOptions, ct)
            .ConfigureAwait(false);

        if (RequiresIdentify(account.ImapHost))
        {
            // 网易系要求认证前发 ID。这里失败**不中断**：ID 只是反垃圾策略的通行证，
            // 真被拒时随后的 AUTH 会报错，届时的错误更准确。
            try
            {
                await _client.IdentifyAsync(BuildClientIdentification(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "向 {Host} 发送 IMAP ID 失败，继续尝试认证", account.ImapHost);
            }
        }

        await _client.AuthenticateAsync(account.Username, credential, ct).ConfigureAwait(false);

        var folder = await _client.GetFolderAsync(InboxFolder, ct).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

        _folder = folder;
    }

    /// <inheritdoc />
    public Task<long> GetMaxUidAsync(CancellationToken ct)
    {
        var folder = RequireFolder();

        // UidNext = 下一个将被分配的值，减一就是当前最大 UID；服务器没给就退回全量 UID SEARCH。
        if (folder.UidNext is { } next)
        {
            return Task.FromResult(Math.Max(0, (long)next.Id - 1));
        }

        return SearchMaxUidAsync(folder, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImapMessage>> FetchSinceAsync(
        long afterUid,
        int limit,
        CancellationToken ct)
    {
        if (limit <= 0)
        {
            return [];
        }

        var folder = RequireFolder();
        var uids = await ResolveUidsAsync(folder, afterUid, limit, ct).ConfigureAwait(false);
        if (uids.Count == 0)
        {
            return [];
        }

        const MessageSummaryItems items =
            MessageSummaryItems.Envelope |
            MessageSummaryItems.BodyStructure |
            MessageSummaryItems.InternalDate |
            MessageSummaryItems.UniqueId;

        var summaries = await folder.FetchAsync(uids, items, ct).ConfigureAwait(false);
        var messages = new List<ImapMessage>(summaries.Count);

        for (var i = 0; i < summaries.Count; i++)
        {
            var summary = summaries[i];

            // 绝大多数服务器会把 UniqueId 带回来；万一没带就按请求顺序对齐（MailKit 保序）。
            var uid = summary.UniqueId.IsValid
                ? summary.UniqueId
                : (i < uids.Count ? uids[i] : default);
            if (!uid.IsValid)
            {
                _logger?.Debug("有一封邮件拿不到有效 UID，跳过：{Subject}", summary.Envelope?.Subject);
                continue;
            }

            var body = await ReadBodyTextAsync(folder, summary, uid, ct).ConfigureAwait(false);
            messages.Add(MapMessage(summary, uid, body));
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct)
    {
        _folder = null;
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "IMAP 会话关闭时出错（忽略）");
        }
        finally
        {
            _folder = null;
            _client.Dispose();
        }
    }

    /// <summary>
    /// 把「要拉哪些 UID」算出来：首次同步取最新一页，增量取水位之后的一页。
    /// </summary>
    private static async Task<IList<UniqueId>> ResolveUidsAsync(
        IMailFolder folder,
        long afterUid,
        int limit,
        CancellationToken ct)
    {
        if (afterUid > 0)
        {
            if (afterUid >= uint.MaxValue)
            {
                return [];
            }

            // 水位之后的全部候选；UID 稀疏时可能不足 limit，这是正常的（取到多少算多少）。
            var range = new UniqueIdRange(new UniqueId((uint)afterUid + 1), UniqueId.MaxValue);
            var found = await folder.SearchAsync(SearchQuery.Uids(range), ct).ConfigureAwait(false);

            return found.Count <= limit ? found : found.Take(limit).ToArray();
        }

        // 首次同步：从**最新**往回取一页。取最旧的一页等于从十年前的邮件开始扫。
        var all = await folder.SearchAsync(SearchQuery.All, ct).ConfigureAwait(false);
        if (all.Count <= limit)
        {
            return all;
        }

        return all.Skip(all.Count - limit).ToArray();
    }

    /// <summary>服务器没给 UidNext 时的兜底：全量 UID SEARCH 取最大值。</summary>
    private static async Task<long> SearchMaxUidAsync(IMailFolder folder, CancellationToken ct)
    {
        var all = await folder.SearchAsync(SearchQuery.All, ct).ConfigureAwait(false);
        return all.Count == 0 ? 0 : (long)all[all.Count - 1].Id;
    }

    /// <summary>取正文：优先纯文本，只有 HTML 就剥离；都没有返回空串。</summary>
    private async Task<string> ReadBodyTextAsync(
        IMailFolder folder,
        IMessageSummary summary,
        UniqueId uid,
        CancellationToken ct)
    {
        var isPlainText = summary.TextBody is not null;
        var part = summary.TextBody ?? summary.HtmlBody;
        if (part is null)
        {
            return string.Empty;
        }

        var entity = await folder.GetBodyPartAsync(uid, part, ct).ConfigureAwait(false);

        return entity switch
        {
            TextPart text when isPlainText => text.Text ?? string.Empty,
            TextPart text => HtmlToText(text.Text ?? string.Empty),
            // rfc822 嵌套邮件（转发成附件那种）：取内层正文，仍然不碰它的附件。
            MessagePart nested => nested.Message?.TextBody ?? string.Empty,
            _ => string.Empty,
        };
    }

    /// <summary>把 <see cref="IMessageSummary"/> 映射成 <see cref="ImapMessage"/>。</summary>
    private static ImapMessage MapMessage(IMessageSummary summary, UniqueId uid, string body)
    {
        var envelope = summary.Envelope;
        var from = envelope?.From.FirstOrDefault();
        var fromAddress = from switch
        {
            MailboxAddress mailbox => mailbox.Address,
            null => string.Empty,
            _ => from.ToString(),
        };

        // Date 头是邮件的「发生时间」；没有才退回服务器的接收时间，最后才用本地时钟兜底。
        var sentAt = envelope?.Date ?? summary.Date;
        if (sentAt == default)
        {
            sentAt = summary.InternalDate ?? DateTimeOffset.UtcNow;
        }

        return new ImapMessage(
            Uid: (long)uid.Id,
            MessageId: string.IsNullOrWhiteSpace(envelope?.MessageId) ? null : envelope.MessageId,
            Subject: envelope?.Subject ?? string.Empty,
            FromAddress: fromAddress,
            FromName: (from as MailboxAddress)?.Name,
            BodyText: body,
            HasAttachments: summary.Attachments.Any(),
            SentAt: sentAt);
    }

    /// <summary>该主机是否要求认证前发 IMAP <c>ID</c>（网易系）。</summary>
    private static bool RequiresIdentify(string host)
    {
        foreach (var domain in IdentifyRequiredDomains)
        {
            if (host.EndsWith(domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>客户端标识：只放产品名与版本，**不含任何可识别用户/机器的字段**。</summary>
    private static ImapImplementation BuildClientIdentification() => new()
    {
        Name = "ZipperAIQQ",
        Version = "1.0",
        Vendor = "ZipperAIQQ",
    };

    /// <summary>
    /// 简易 HTML → 纯文本（够用即可，**不为它引第三个包**）。
    /// </summary>
    /// <remarks>
    /// 顺序很重要：先剥标签再解实体。反过来的话正文里被转义的 <c>&lt;script&gt;</c>
    /// 会被当成真标签整段删掉。
    /// </remarks>
    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = CommentRegex.Replace(html, string.Empty);
        text = ScriptStyleRegex.Replace(text, string.Empty);
        text = LineBreakTagRegex.Replace(text, "\n");
        text = TagRegex.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = HorizontalSpaceRegex.Replace(text, " ");
        text = BlankLinesRegex.Replace(text, "\n\n");

        return text.Trim();
    }

    /// <summary>取已打开的收件箱；未连接时是调用方用错了（显式失败，别静默空转）。</summary>
    private IMailFolder RequireFolder() =>
        _folder ?? throw new InvalidOperationException("IMAP 会话尚未连接：请先调用 ConnectAsync。");
}

/// <summary>
/// <see cref="IImapSessionFactory"/> 的默认实现。
/// </summary>
/// <remarks>
/// <para>
/// 无参构造是给 <c>services.AddSingleton&lt;IImapSessionFactory, MailKitImapSessionFactory&gt;()</c>
/// 与手工 new 用的；带 <see cref="ILogger"/> 的重载让容器注进来的日志能一路传到会话里
/// （否则 ID 发送失败、断开失败这些诊断就只能静默）。
/// </para>
/// <para>
/// <see cref="Create"/> 每次返回**新会话**：<c>ImapClient</c> 不保证线程安全，
/// 共享它等于把跨账号的并发问题焊死在实现里。
/// </para>
/// </remarks>
public sealed class MailKitImapSessionFactory : IImapSessionFactory
{
    private readonly ILogger? _logger;

    /// <summary>无参构造：不记录会话级诊断。</summary>
    public MailKitImapSessionFactory()
    {
    }

    /// <summary>由容器注入日志（DI 会优先选这个构造）。</summary>
    /// <param name="logger">Serilog 日志。</param>
    public MailKitImapSessionFactory(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public IImapSession Create() => new MailKitImapSession(_logger);
}
