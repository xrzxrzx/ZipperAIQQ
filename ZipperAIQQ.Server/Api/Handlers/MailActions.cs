using System.Text.Json;
using Serilog;
using ZipperAIQQ.Mail;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Mail;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>邮件的读接口：列表（可按账号过滤）与单封详情。</summary>
/// <remarks>
/// 邮件**不进 <c>Messages</c> 表**（它是通知流，不是对话流），所以这里走独立的邮件仓储，
/// 不复用 <c>getMessages</c>。
/// </remarks>
public sealed class ListMailsAction : IApiAction
{
    private readonly IMailRepository _repository;

    public ListMailsAction(IMailRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public string Name => "listMails";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var accountId = PayloadReader.GetString(payload, "accountId");
        var limit = Math.Clamp(PayloadReader.GetInt(payload, "limit", 50), 1, 200);

        var rows = await _repository
            .ListAsync(string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim(), limit, ct)
            .ConfigureAwait(false);

        return rows
            .Select(mail => new
            {
                id = mail.Id,
                accountId = mail.AccountId,
                messageId = mail.MessageId,
                subject = mail.Subject,
                fromAddress = mail.FromAddress,
                fromName = mail.FromName,
                hasAttachments = mail.HasAttachments,
                sentAt = mail.SentAt.ToUnixTimeMilliseconds(),
                receivedAt = mail.ReceivedAt.ToUnixTimeMilliseconds(),
                summary = MailViews.Summary(mail.Summary),
            })
            .ToList();
    }
}

/// <summary>单封邮件详情（**正文按 <c>mail.maxBodyChars</c> 截断**后返回）。</summary>
/// <remarks>
/// 截断在服务端做而不是让客户端处理：一封 20000 字的邮件整段丢给 HTTP，
/// 只为了显示前几十行，纯属浪费带宽与内存。
/// </remarks>
public sealed class GetMailAction : IApiAction
{
    private readonly IMailRepository _repository;
    private readonly MailOptions _options;

    public GetMailAction(IMailRepository repository, MailOptions options)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(options);

        _repository = repository;
        _options = options;
    }

    public string Name => "getMail";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var id = MailPayload.ReadLong(payload, "id")
            ?? throw new ApiException(ApiResponse.BadRequest, "payload 需要 id（邮件主键）。");

        var mail = await _repository.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new ApiException(ApiResponse.NotFound, $"邮件不存在：{id}");

        var maxChars = Math.Max(1, _options.MaxBodyChars);
        var body = MailPayload.Truncate(mail.BodyText, maxChars);

        return new
        {
            id = mail.Id,
            accountId = mail.AccountId,
            messageId = mail.MessageId,
            subject = mail.Subject,
            fromAddress = mail.FromAddress,
            fromName = mail.FromName,
            hasAttachments = mail.HasAttachments,
            sentAt = mail.SentAt.ToUnixTimeMilliseconds(),
            receivedAt = mail.ReceivedAt.ToUnixTimeMilliseconds(),
            summary = MailViews.Summary(mail.Summary),
            body = body.Text,
            bodyTruncated = body.IsTruncated,
            bodyLength = mail.BodyText.Length,
            maxBodyChars = maxChars,
        };
    }
}

/// <summary>收信账号列表。</summary>
/// <remarks>
/// ⚠️ **永不回显凭据**（邮件接入设计 §4.1 / 验收标准 6）：只给 <c>hasSecret</c>，
/// 密文与明文都不出现在响应里。客户端要改授权码就整条重填。
/// </remarks>
public sealed class ListMailAccountsAction : IApiAction
{
    private readonly IMailRepository _repository;

    public ListMailAccountsAction(IMailRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public string Name => "listMailAccounts";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var accounts = await _repository.LoadAccountsAsync(ct).ConfigureAwait(false);

        return accounts.Select(MailViews.Account).ToList();
    }
}

/// <summary>
/// 新增 / 修改一个收信账号。
/// </summary>
/// <remarks>
/// <para>
/// 语义与 <c>setEndpoint</c> 一致：**没给的字段沿用库里的值**，
/// 给了 <c>secret</c> 才重新加密覆盖（授权码轮换时不要求把整条记录重填）。
/// </para>
/// <para>
/// 新账号的 <c>id</c> 自动生成（<c>qq-1</c> / <c>163-1</c> 这种短 ID），
/// 这样日志与文件名里读起来是人话，而不是一串 GUID。
/// </para>
/// </remarks>
public sealed class SaveMailAccountAction : IApiAction
{
    private readonly IMailRepository _repository;
    private readonly ICredentialProtector _protector;
    private readonly IConfigStore _configs;
    private readonly ILogger _logger;

    public SaveMailAccountAction(
        IMailRepository repository,
        ICredentialProtector protector,
        IConfigStore configs,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _protector = protector;
        _configs = configs;
        _logger = logger;
    }

    public string Name => "saveMailAccount";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var rawId = PayloadReader.GetString(payload, "id");
        var id = string.IsNullOrWhiteSpace(rawId) ? null : rawId.Trim();

        var accounts = await _repository.LoadAccountsAsync(ct).ConfigureAwait(false);

        MailAccount? existing = null;
        if (id is not null)
        {
            existing = accounts.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))
                ?? throw new ApiException(ApiResponse.NotFound, $"收信账号不存在：{id}");
        }

        var address = Merge(payload, "address", existing?.Address);
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 address（邮箱地址）。");
        }

        address = address.Trim();
        if (!address.Contains('@', StringComparison.Ordinal) || address.Any(char.IsWhiteSpace))
        {
            throw new ApiException(ApiResponse.BadRequest, $"address 不是合法邮箱地址：{address}");
        }

        var imapHost = Merge(payload, "imapHost", existing?.ImapHost);
        if (string.IsNullOrWhiteSpace(imapHost))
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 imapHost（如 imap.qq.com）。");
        }

        imapHost = imapHost.Trim();
        if (imapHost.Any(char.IsWhiteSpace))
        {
            throw new ApiException(ApiResponse.BadRequest, "imapHost 不能包含空白字符。");
        }

        var imapPort = MailPayload.ReadInt(payload, "imapPort") ?? existing?.ImapPort ?? 993;
        if (imapPort is < 1 or > 65535)
        {
            throw new ApiException(ApiResponse.BadRequest, $"imapPort 超出范围（1-65535）：{imapPort}");
        }

        var useSsl = MailPayload.ReadBool(payload, "useSsl") ?? existing?.UseSsl ?? true;
        var enabled = MailPayload.ReadBool(payload, "enabled") ?? existing?.Enabled ?? true;
        var displayName = Merge(payload, "displayName", existing?.DisplayName);
        var username = Merge(payload, "username", existing?.Username);
        username = string.IsNullOrWhiteSpace(username) ? address : username.Trim();

        // secret：给了就重新加密覆盖；没给（或给空串）保留旧密文。
        // 明文只在这一行里存在，之后整个系统只见密文。
        var secret = PayloadReader.GetString(payload, "secret");
        var secretChanged = !string.IsNullOrWhiteSpace(secret);
        var secretCipher = existing?.SecretCipher;

        if (secretChanged)
        {
            secretCipher = _protector.Protect(secret!.Trim());
        }
        else if (string.IsNullOrEmpty(secretCipher))
        {
            throw new ApiException(
                ApiResponse.BadRequest,
                "新账号需要 secret（邮箱授权码，不是登录密码）。");
        }

        var accountId = existing?.Id
            ?? MailPayload.NewAccountId(accounts.Select(a => a.Id).ToList(), imapHost);

        var account = new MailAccount
        {
            Id = accountId,
            Address = address,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            ImapHost = imapHost,
            ImapPort = imapPort,
            UseSsl = useSsl,
            Username = username,
            SecretCipher = secretCipher!,
            Enabled = enabled,
            // 同步水位与最近错误属于"运行状态"，不该被一次配置保存冲掉
            LastUid = existing?.LastUid ?? 0,
            LastSyncAt = existing?.LastSyncAt,
            LastError = existing?.LastError,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
        };

        await _repository.UpsertAccountAsync(account, ct).ConfigureAwait(false);

        // 审计只记"谁对哪个账号做了什么"，**绝不记凭据内容**
        await _configs
            .AppendAuditAsync(
                secretChanged ? "saveMailAccount(+secret)" : "saveMailAccount",
                "http-client",
                account.Id,
                ct)
            .ConfigureAwait(false);

        _logger.Information(
            "收信账号已保存：{AccountId}（{Host}:{Port}，启用={Enabled}，凭据={SecretState}）",
            account.Id, account.ImapHost, account.ImapPort, account.Enabled,
            secretChanged ? "已更新" : "未变");

        return MailViews.Account(account);
    }

    /// <summary>取字符串字段；没给（或空白）时用 <paramref name="fallback"/>。</summary>
    private static string? Merge(JsonElement payload, string name, string? fallback)
    {
        var value = PayloadReader.GetString(payload, name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}

/// <summary>删除一个收信账号（连带它的邮件与摘要，同一事务，不留孤儿行）。</summary>
public sealed class DeleteMailAccountAction : IApiAction
{
    private readonly IMailRepository _repository;
    private readonly IConfigStore _configs;
    private readonly ILogger _logger;

    public DeleteMailAccountAction(IMailRepository repository, IConfigStore configs, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(logger);

        _repository = repository;
        _configs = configs;
        _logger = logger;
    }

    public string Name => "deleteMailAccount";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var rawId = PayloadReader.GetString(payload, "id");
        if (string.IsNullOrWhiteSpace(rawId))
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 id（收信账号 ID）。");
        }

        var id = rawId.Trim();
        var accounts = await _repository.LoadAccountsAsync(ct).ConfigureAwait(false);
        var account = accounts.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))
            ?? throw new ApiException(ApiResponse.NotFound, $"收信账号不存在：{id}");

        await _repository.DeleteAccountAsync(id, ct).ConfigureAwait(false);

        await _configs
            .AppendAuditAsync("deleteMailAccount", "http-client", id, ct)
            .ConfigureAwait(false);

        _logger.Information("收信账号已删除：{AccountId}（{Address}）", id, account.Address);

        return new { deleted = true, id, address = account.Address };
    }
}

/// <summary>
/// 手动触发一次 IMAP 同步（客户端"立即收信"按钮）。
/// </summary>
/// <remarks>
/// <para>
/// 动作落在 <see cref="MailSource.ReloadAsync"/> 上：它是「全停再全起」的热重建，
/// 因此 <b>整个源都会重连</b>，<c>accountId</c> 只用来校验「这个账号到底存不存在」——
/// 免得客户端以为自己只同步了一个账号，实际却动了全部。
/// </para>
/// <para>
/// 重连是后台行为：这里返回 <c>triggered = true</c> 只代表"重建已被执行"，
/// 不代表新邮件已经到库（真要等结果请看 WS 事件或列表）。
/// </para>
/// </remarks>
public sealed class SyncMailAction : IApiAction
{
    private readonly MailSource _source;
    private readonly IMailRepository _repository;
    private readonly ILogger _logger;

    public SyncMailAction(MailSource source, IMailRepository repository, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);

        _source = source;
        _repository = repository;
        _logger = logger;
    }

    public string Name => "syncMail";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var rawAccountId = PayloadReader.GetString(payload, "accountId");
        var accountId = string.IsNullOrWhiteSpace(rawAccountId) ? null : rawAccountId.Trim();

        IReadOnlyList<MailAccount> accounts = await _repository.LoadAccountsAsync(ct).ConfigureAwait(false);

        if (accountId is not null &&
            !accounts.Any(a => string.Equals(a.Id, accountId, StringComparison.Ordinal)))
        {
            // 明确指定了却不存在：400 + 原因，而不是"假装同步了一个不存在的账号"
            throw new ApiException(ApiResponse.BadRequest, $"收信账号不存在：{accountId}");
        }

        _logger.Information(
            "收到手动同步请求：账号={AccountId} 已启用={EnabledCount}",
            accountId ?? "(全部)",
            accounts.Count(a => a.Enabled));

        await _source.ReloadAsync(ct).ConfigureAwait(false);

        return new
        {
            triggered = true,
            accountId,
            accounts = accounts.Where(a => a.Enabled).Select(a => a.Id).ToList(),
        };
    }
}

/// <summary>邮件相关的响应投影与 payload 读取小工具（只在这个文件里用）。</summary>
internal static class MailViews
{
    /// <summary>摘要行 → 响应对象；没摘要时给 <c>null</c>（与"摘要失败"区分得开）。</summary>
    public static object? Summary(MailSummaryRow? row) => row is null
        ? null
        : new
        {
            text = row.Text,
            needsAction = row.NeedsAction,
            status = row.Status,
            error = row.Error,
            updatedAt = row.UpdatedAt.ToUnixTimeMilliseconds(),
            forwardedAt = row.ForwardedAt?.ToUnixTimeMilliseconds(),
        };

    /// <summary>账号 → 响应对象。**只有 <c>hasSecret</c>，永不含 <c>SecretCipher</c>**。</summary>
    public static object Account(MailAccount account) => new
    {
        id = account.Id,
        address = account.Address,
        displayName = account.DisplayName,
        imapHost = account.ImapHost,
        imapPort = account.ImapPort,
        useSsl = account.UseSsl,
        username = account.Username,
        hasSecret = !string.IsNullOrEmpty(account.SecretCipher),
        enabled = account.Enabled,
        lastUid = account.LastUid,
        lastSyncAt = account.LastSyncAt?.ToUnixTimeMilliseconds(),
        lastError = account.LastError,
        createdAt = account.CreatedAt.ToUnixTimeMilliseconds(),
    };
}

/// <summary>payload 的宽容读取 + 正文截断。</summary>
internal static class MailPayload
{
    /// <summary>读整数；没给返回 <c>null</c>，给了但不是数字直接 400（不静默当没给）。</summary>
    public static long? ReadLong(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String &&
            long.TryParse(element.GetString(), out var parsed))
        {
            return parsed;
        }

        throw new ApiException(ApiResponse.BadRequest, $"{name} 必须是整数。");
    }

    /// <summary>读整数（int 范围）；没给返回 <c>null</c>，类型不对 400。</summary>
    public static int? ReadInt(JsonElement payload, string name)
    {
        var value = ReadLong(payload, name);
        if (value is null)
        {
            return null;
        }

        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new ApiException(ApiResponse.BadRequest, $"{name} 超出整数范围：{value}");
        }

        return (int)value;
    }

    /// <summary>读布尔；没给返回 <c>null</c>，类型不对 400。</summary>
    public static bool? ReadBool(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ApiException(ApiResponse.BadRequest, $"{name} 必须是 true / false。"),
        };
    }

    /// <summary>按字符截断，且不把代理对切断（切一半就是乱码方块）。</summary>
    public static (string Text, bool IsTruncated) Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return (text ?? string.Empty, false);
        }

        var cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return (text[..cut], true);
    }

    /// <summary>
    /// 新账号短 ID：<c>imap.qq.com</c> → <c>qq-1</c>、<c>imap.163.com</c> → <c>163-1</c>。
    /// 已有同前缀就往后取号；极端情况（号段耗尽）用随机后缀兜底。
    /// </summary>
    public static string NewAccountId(IReadOnlyList<string> existingIds, string imapHost)
    {
        var prefix = IdPrefix(imapHost);
        var used = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i <= 999; i++)
        {
            var candidate = $"{prefix}-{i}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        // 号段耗尽基本不可能（999 个同前缀账号），但绝不返回重复 ID
        return $"{prefix}-{Guid.NewGuid().ToString("N")[..6]}";
    }

    private static string IdPrefix(string imapHost)
    {
        var parts = imapHost.Trim().ToLowerInvariant()
            .Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return "mail";
        }

        // imap.qq.com → qq；imap.163.com → 163；outlook.office365.com → outlook
        var label = parts[0] is "imap" or "pop" or "smtp" or "mail" && parts.Length > 1
            ? parts[1]
            : parts[0];

        var sanitized = new string(label.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return sanitized.Length == 0 ? "mail" : sanitized;
    }
}
