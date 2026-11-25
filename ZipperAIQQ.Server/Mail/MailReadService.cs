using Serilog;
using ZipperAIQQ.Agent.Mail;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Server.Mail;

/// <summary>
/// <see cref="IMailReader"/> 的服务端实现：把「找邮件 → 取素材 → 渲染 / 读原文」串起来。
/// </summary>
/// <remarks>
/// <para>
/// 分工：位置与落库内容在仓储（<see cref="IMailRepository"/>），取邮件本体的连接在
/// <see cref="MailImapAccess"/>，截图在 <see cref="MailRenderService"/>。本类只管
/// 「先看库里有没有、没有再去服务器要」这条顺序，以及把失败翻成人话。
/// </para>
/// <para>
/// **优先用库里的 <see cref="StoredMail.BodyHtml"/>**：收信时就留了底，
/// 多数情况根本不用连 IMAP（也就不用担心 163 已经把邮件清了）。
/// </para>
/// </remarks>
public sealed class MailReadService : IMailReader
{
    /// <summary>读原文的硬上限：配置写多大都不许超过它（防一次把整封几十 MB 塞进上下文）。</summary>
    private const int MaxRawCharsCeiling = 200_000;

    private readonly IMailRepository _repository;
    private readonly MailImapAccess _imap;
    private readonly IMailRenderer _renderer;
    private readonly MailOptions _options;
    private readonly ILogger _logger;

    public MailReadService(
        IMailRepository repository,
        MailImapAccess imap,
        IMailRenderer renderer,
        MailOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(imap);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _imap = imap;
        _renderer = renderer;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanRender => _options.RenderEnabled && _renderer.IsAvailable;

    /// <inheritdoc />
    public string RenderUnavailableReason
    {
        get
        {
            if (!_options.RenderEnabled)
            {
                return "邮件画面通道没开（服务端 mail.renderEnabled=false）。";
            }

            return _renderer.IsAvailable ? string.Empty : _renderer.UnavailableReason;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailBrief>> LocateAsync(string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return [];
        }

        var trimmed = reference.Trim();

        // 纯数字 = 主键直取（模型从工具结果里拿到的就是它）
        if (long.TryParse(trimmed, out var id) && id > 0)
        {
            var byId = await _repository.GetAsync(id, ct).ConfigureAwait(false);
            return byId is null ? [] : [ToBrief(byId)];
        }

        var hits = await _repository.SearchAsync(null, trimmed, 10, ct).ConfigureAwait(false);
        return hits.Select(ToBrief).ToList();
    }

    /// <inheritdoc />
    public async Task<MailRenderOutcome> RenderAsync(
        long mailId, bool segments, CancellationToken ct = default)
    {
        if (!CanRender)
        {
            return MailRenderOutcome.Failed(RenderUnavailableReason);
        }

        var mail = await _repository.GetAsync(mailId, ct).ConfigureAwait(false);
        if (mail is null)
        {
            return MailRenderOutcome.Failed($"邮件 #{mailId} 不在库里（可能已被清理）。");
        }

        string? html;
        try
        {
            html = ResolveStoredHtml(mail);
            if (string.IsNullOrWhiteSpace(html))
            {
                html = await FetchHtmlFromServerAsync(mail, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "取邮件 HTML 失败：mailId={MailId}", mailId);
            return MailRenderOutcome.Failed($"取这封邮件的 HTML 失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            return MailRenderOutcome.Failed(
                "这封邮件没有 HTML 正文（纯文本邮件），没有版式可看；直接用正文摘要回答就好。");
        }

        try
        {
            var rendered = await _renderer.RenderAsync(html, segments, ct).ConfigureAwait(false);
            var note = BuildRenderNote(rendered);
            return new MailRenderOutcome(rendered.Images, note, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "渲染邮件失败：mailId={MailId}", mailId);
            return MailRenderOutcome.Failed($"渲染失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<MailRawOutcome> ReadRawAsync(
        long mailId, int maxChars, CancellationToken ct = default)
    {
        var mail = await _repository.GetAsync(mailId, ct).ConfigureAwait(false);
        if (mail is null)
        {
            return MailRawOutcome.Failed($"邮件 #{mailId} 不在库里（可能已被清理）。");
        }

        if (mail.Uid is not { } uid)
        {
            // 旧版本收下的邮件没有 UID 列：这是"当时没存"，不是故障，说清楚就行
            return MailRawOutcome.Failed(
                "这封邮件是升级前收下的，没有记录 IMAP UID，读不到原文；正文摘要仍然可用。");
        }

        var limit = Math.Clamp(
            maxChars > 0 ? maxChars : _options.RawMaxChars,
            1,
            MaxRawCharsCeiling);

        try
        {
            var raw = await _imap.GetRawAsync(mail.AccountId, uid, limit, ct).ConfigureAwait(false);
            return new MailRawOutcome(raw.Mime, raw.Headers, raw.Body, raw.Truncated, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读邮件原文失败：mailId={MailId} uid={Uid}", mailId, uid);
            return MailRawOutcome.Failed($"读原文失败：{ex.Message}");
        }
    }

    /// <summary>取库里存的 HTML；超过上限的部分已经不重要（渲染不看超长尾巴）。</summary>
    private string? ResolveStoredHtml(StoredMail mail) =>
        string.IsNullOrWhiteSpace(mail.BodyHtml) ? null : mail.BodyHtml;

    /// <summary>库里没留底（升级前的邮件）时，去服务器上再要一次。</summary>
    private async Task<string?> FetchHtmlFromServerAsync(StoredMail mail, CancellationToken ct)
    {
        if (mail.Uid is not { } uid)
        {
            return null;
        }

        _logger.Information(
            "邮件 #{MailId} 库里没有 HTML 留底，改从 IMAP 取（uid={Uid}）", mail.Id, uid);

        var html = await _imap
            .GetHtmlAsync(mail.AccountId, uid, _options.RenderMaxBodyHtmlKb * 1024, ct)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(html) ? null : html;
    }

    private static string BuildRenderNote(RenderedMail rendered)
    {
        var parts = new List<string> { $"渲染引擎 {rendered.Engine}" };

        if (rendered.PageHeight > 0)
        {
            parts.Add($"整页高 {rendered.PageHeight}px");
        }

        if (rendered.Truncated)
        {
            parts.Add("正文较长，只给了前几段（要看更靠下的部分，再调一次并说明要看哪里）");
        }

        return "（" + string.Join("；", parts) + "）";
    }

    private static MailBrief ToBrief(StoredMail mail) => new(
        mail.Id,
        mail.Subject,
        string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress : $"{mail.FromName} <{mail.FromAddress}>",
        mail.SentAt,
        !string.IsNullOrWhiteSpace(mail.BodyHtml),
        mail.Uid is not null);
}
