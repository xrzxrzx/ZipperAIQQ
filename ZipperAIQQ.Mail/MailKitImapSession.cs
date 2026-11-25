using System.Net;
using System.Text;
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

    /// <summary>原文读取时头部文本的上限（字符）：Received 链能堆几十层，全抄进上下文纯烧钱。</summary>
    private const int MaxRawHeaderChars = 8000;

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

            var (bodyText, bodyHtml) = await ReadBodyAsync(folder, summary, uid, ct).ConfigureAwait(false);
            messages.Add(MapMessage(summary, uid, bodyText, bodyHtml));
        }

        return messages;
    }

    /// <inheritdoc />
    public async Task<string?> GetHtmlAsync(long uid, int maxChars, CancellationToken ct)
    {
        var folder = RequireFolder();
        var id = ToUniqueId(uid);

        var structure = await GetStructureAsync(folder, id, ct).ConfigureAwait(false);
        var htmlPart = FindHtmlPart(structure);
        if (htmlPart is null)
        {
            return null;
        }

        var entity = await folder.GetBodyPartAsync(id, htmlPart, ct).ConfigureAwait(false);
        var html = ExtractText(entity);
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        return maxChars > 0 && html.Length > maxChars ? html[..SafeCut(html, maxChars)] : html;
    }

    /// <inheritdoc />
    public async Task<MailRawContent> GetRawAsync(long uid, int maxChars, CancellationToken ct)
    {
        var folder = RequireFolder();
        var id = ToUniqueId(uid);

        // 头：**全量**取（Received 链是排查伪造发件人的关键证据），但整体也给个上限 ——
        // 有些垃圾邮件堆了几十层 Received，逐层抄进上下文纯属烧钱。
        var headerList = await folder.GetHeadersAsync(id, ct).ConfigureAwait(false);
        var headers = new StringBuilder();
        var mime = string.Empty;

        foreach (var header in headerList)
        {
            if (header.Id == HeaderId.ContentType && string.IsNullOrEmpty(mime))
            {
                mime = header.Value ?? string.Empty;
            }

            if (headers.Length >= MaxRawHeaderChars)
            {
                headers.AppendLine("…（头过长，已截断）");
                break;
            }

            headers.Append(header.Field).Append(": ").AppendLine(header.Value);
        }

        // 正文：只取**文本部分**（text/plain、text/html），附件一个字节都不下 ——
        // 30 MB 的附件邮件整封下载会把内存顶上去了（服务端只有 8 GB）。
        var structure = await GetStructureAsync(folder, id, ct).ConfigureAwait(false);
        var parts = TextParts(structure)
            .OrderBy(part => part.IsHtml ? 1 : 0) // 纯文本在前，HTML 源文本在后
            .ToList();

        var body = new StringBuilder();
        var truncated = false;

        foreach (var part in parts)
        {
            if (body.Length >= maxChars)
            {
                truncated = true;
                break;
            }

            var entity = await folder.GetBodyPartAsync(id, part, ct).ConfigureAwait(false);
            var text = ExtractText(entity);
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var label = part.IsHtml ? "[text/html 源文本]" : "[text/plain]";
            var remaining = maxChars - body.Length;
            if (text.Length > remaining)
            {
                body.AppendLine(label).AppendLine(text[..SafeCut(text, remaining)]);
                truncated = true;
                break;
            }

            body.AppendLine(label).AppendLine(text);
        }

        return new MailRawContent(mime, headers.ToString().TrimEnd(), body.ToString().TrimEnd(), truncated);
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

            // ⚠️ 不信服务器的范围过滤：实测 163 会返回**旧 UID**（甚至把整页原样重投）。
            // 自己再筛一遍并取**最新**的 limit 条 —— 拿最旧的等于永远追不上新的。
            var fresh = found.Where(uid => uid.Id > (uint)afterUid).ToArray();
            return fresh.Length <= limit ? fresh : fresh[(fresh.Length - limit)..];
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

    /// <summary>取正文：优先纯文本；同时把 HTML 原文带回来（按需渲染用）。</summary>
    /// <remarks>
    /// 两个部分**各取一次**而不是"有纯文本就不看 HTML"：渲染要的是 HTML 版式，
    /// 而纯文本那份是餵模型的。163 的典型邮件两个部分都在。
    /// </remarks>
    private async Task<(string Text, string? Html)> ReadBodyAsync(
        IMailFolder folder,
        IMessageSummary summary,
        UniqueId uid,
        CancellationToken ct)
    {
        string? plain = null;
        string? html = null;

        if (summary.TextBody is { } textPart)
        {
            var entity = await folder.GetBodyPartAsync(uid, textPart, ct).ConfigureAwait(false);
            plain = ExtractText(entity);
        }

        if (summary.HtmlBody is { } htmlPart)
        {
            var entity = await folder.GetBodyPartAsync(uid, htmlPart, ct).ConfigureAwait(false);
            html = ExtractText(entity);
        }

        // 没有纯文本部分时，正文只能由 HTML 剥离而来（既有口径，保持不变）。
        var text = plain ?? (html is null ? string.Empty : HtmlToText(html));
        return (text, string.IsNullOrEmpty(html) ? null : html);
    }

    /// <summary>把 <see cref="IMessageSummary"/> 映射成 <see cref="ImapMessage"/>。</summary>
    private static ImapMessage MapMessage(
        IMessageSummary summary, UniqueId uid, string body, string? html)
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
            SentAt: sentAt)
        {
            BodyHtml = html,
        };
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

    /// <summary>Uid 转 <see cref="UniqueId"/>：越界（uint 之外）说明库里的值坏了，直接拒。</summary>
    private static UniqueId ToUniqueId(long uid)
    {
        if (uid is <= 0 or > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(uid), uid, "IMAP UID 超出 uint 范围，无法定位邮件。");
        }

        return new UniqueId((uint)uid);
    }

    /// <summary>
    /// 取单封邮件的 MIME 结构树（按需读取用：先看结构，才知道正文在哪一层）。
    /// </summary>
    /// <remarks>
    /// 用 <c>Fetch(uid, BodyStructure)</c> 而不是某个 <c>GetBodyStructure</c>：
    /// MailKit 的 <see cref="IMailFolder"/> 上只有 fetch 这条口子（消息概览里那个
    /// <see cref="IMessageSummary.Body"/> 就是这么来的）。结构体很小，不涉及正文下载。
    /// </remarks>
    private static async Task<BodyPart?> GetStructureAsync(
        IMailFolder folder, UniqueId uid, CancellationToken ct)
    {
        var summaries = await folder
            .FetchAsync([uid], MessageSummaryItems.BodyStructure, ct)
            .ConfigureAwait(false);

        return summaries.Count == 0 ? null : summaries[0].Body;
    }

    /// <summary>在一棵 body 结构树里找 <c>text/html</c> 部分（广度上找第一个）。</summary>
    private static BodyPartText? FindHtmlPart(BodyPart? structure)
    {
        foreach (var part in TextParts(structure))
        {
            if (part.IsHtml)
            {
                return part;
            }
        }

        return null;
    }

    /// <summary>递归列出所有文本部分（<c>text/plain</c> + <c>text/html</c>），跳过附件。</summary>
    private static IEnumerable<BodyPartText> TextParts(BodyPart? part)
    {
        switch (part)
        {
            case BodyPartText text:
                yield return text;
                break;

            case BodyPartMultipart multipart:
                foreach (var child in multipart.BodyParts)
                {
                    foreach (var nested in TextParts(child))
                    {
                        yield return nested;
                    }
                }

                break;

            // rfc822 嵌套（转发成附件的那种）：正文在内层，仍然不碰它的附件。
            case BodyPartMessage message:
                foreach (var nested in TextParts(message.Body))
                {
                    yield return nested;
                }

                break;
        }
    }

    /// <summary>从取回的实体里拿文本；嵌套邮件取内层纯文本。</summary>
    private static string? ExtractText(MimeEntity entity) => entity switch
    {
        TextPart text => text.Text ?? string.Empty,
        MessagePart nested => nested.Message?.TextBody,
        _ => null,
    };

    /// <summary>安全截断位置：落点正好在高代理上时退一格（见 <see cref="HtmlToText"/> 上游的说明）。</summary>
    private static int SafeCut(string text, int max) =>
        max > 0 && max < text.Length && char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
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
