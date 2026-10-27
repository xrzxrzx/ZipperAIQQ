using System.Text.Json;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Server.Channels;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Mail;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Mails;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 「第三方平台接入」页的数据（决策七 / S3-F）。
/// </summary>
/// <remarks>
/// <para>
/// 页面要三样东西：**平台总览**（谁连着、selfId 是什么）、**邮件账号列表**、**抖音 sidecar 配置与状态**。
/// 邮件账号的增删改直接用既有的 <c>listMailAccounts</c> / <c>saveMailAccount</c> 等 action，
/// 这里不重复造一套（那会变成两份口径，改一处忘一处）。
/// </para>
/// <para>
/// <b>凭据一律不回显</b>：邮件只给 <c>hasSecret</c>，抖音只给 <c>tokenConfigured</c>。
/// </para>
/// </remarks>
public sealed class GetPlatformsAction : IApiAction
{
    private readonly IMessageChannel _channel;
    private readonly DouyinChannel _douyin;
    private readonly DouyinOptionsService _douyinOptions;
    private readonly IMailRepository _mailRepository;
    private readonly MailOptions _mailOptions;
    private readonly MailOptionsService _mailSwitch;

    public GetPlatformsAction(
        IMessageChannel channel,
        DouyinChannel douyin,
        DouyinOptionsService douyinOptions,
        IMailRepository mailRepository,
        MailOptions mailOptions,
        MailOptionsService mailSwitch)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(douyin);
        ArgumentNullException.ThrowIfNull(douyinOptions);
        ArgumentNullException.ThrowIfNull(mailRepository);
        ArgumentNullException.ThrowIfNull(mailOptions);
        ArgumentNullException.ThrowIfNull(mailSwitch);

        _channel = channel;
        _douyin = douyin;
        _douyinOptions = douyinOptions;
        _mailRepository = mailRepository;
        _mailOptions = mailOptions;
        _mailSwitch = mailSwitch;
    }

    public string Name => "getPlatforms";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var accounts = await _mailRepository.LoadAccountsAsync(ct).ConfigureAwait(false);
        var douyin = _douyinOptions.Current;

        return new
        {
            platforms = _channel.Platforms
                .Select(platform => new
                {
                    id = platform,
                    label = LabelOf(platform),
                    // "连着"的判据：通道已经知道自己是谁（握手完成）。
                    // QQ 走 NapCat 的 lifecycle 事件、抖音走 hello 帧 —— 对上层是同一个含义。
                    connected = string.Equals(platform, DouyinChannel.PlatformName, StringComparison.OrdinalIgnoreCase)
                        ? _douyin.IsConnected
                        : !string.IsNullOrWhiteSpace(_channel.SelfIdFor(platform)),
                    selfId = _channel.SelfIdFor(platform),
                    enabled = !string.Equals(platform, DouyinChannel.PlatformName, StringComparison.OrdinalIgnoreCase)
                        || douyin.Enabled,
                    features = FeaturesOf(_channel.Capabilities),
                })
                .ToList(),

            mail = new
            {
                // 运行时真值（S3-F 补）：客户端改完开关马上就能看到效果
                enabled = _mailSwitch.Enabled,
                accounts = accounts
                    .Select(a => new
                    {
                        id = a.Id,
                        address = a.Address,
                        displayName = a.DisplayName,
                        imapHost = a.ImapHost,
                        imapPort = a.ImapPort,
                        useSsl = a.UseSsl,
                        username = a.Username,
                        enabled = a.Enabled,
                        lastSyncAt = a.LastSyncAt?.ToUnixTimeMilliseconds(),
                        lastError = a.LastError,
                        // 永不下发密文/明文，只告诉客户端"配没配"
                        hasSecret = !string.IsNullOrEmpty(a.SecretCipher),
                    })
                    .ToList(),
            },

            douyin = new
            {
                enabled = douyin.Enabled,
                connected = _douyin.IsConnected,
                selfId = _douyin.SelfId,
                tokenConfigured = !string.IsNullOrWhiteSpace(douyin.Token),
                allowedSessions = douyin.AllowedSessions,
                sendTimeoutMs = douyin.SendTimeoutMs,
                maxTextLength = douyin.MaxTextLength,
                queryTimeoutMs = douyin.QueryTimeoutMs,
                // sidecar 要连的端点：给客户端显示，便于用户对着配 sidecar
                endpoint = "/ws/channel",
            },
        };
    }

    private static string LabelOf(string platform) => platform.ToLowerInvariant() switch
    {
        "qq" => "QQ",
        "douyin" => "抖音",
        _ => platform,
    };

    private static List<string> FeaturesOf(ChannelCapabilities capabilities)
    {
        var features = new List<string>(4);
        if (capabilities.HasFlag(ChannelCapabilities.Text))
        {
            features.Add("text");
        }

        if (capabilities.HasFlag(ChannelCapabilities.Image))
        {
            features.Add("image");
        }

        if (capabilities.HasFlag(ChannelCapabilities.Mention))
        {
            features.Add("mention");
        }

        if (capabilities.HasFlag(ChannelCapabilities.Reply))
        {
            features.Add("reply");
        }

        return features;
    }
}

/// <summary>
/// 保存抖音 sidecar 配置（决策七）：写库 + 立即生效。
/// </summary>
/// <remarks>
/// payload：<c>{ "enabled": true, "token": "...", "allowedSessions": ["douyin:p*"], "sendTimeoutMs": 15000 }</c>。
/// <c>token</c> 缺省 = 不改；空串 = 清除；其它 = 加密覆盖。**响应永不回显 token**。
/// </remarks>
public sealed class SaveDouyinAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly DouyinOptionsService _options;
    private readonly DouyinChannel _channel;
    private readonly IConfigStore _configs;

    public SaveDouyinAction(DouyinOptionsService options, DouyinChannel channel, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(configs);

        _options = options;
        _channel = channel;
        _configs = configs;
    }

    public string Name => "saveDouyin";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 必须是对象。");
        }

        bool? enabled = null;
        if (payload.TryGetProperty("enabled", out var enabledElement))
        {
            enabled = enabledElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ApiException(ApiResponse.BadRequest, "enabled 必须是布尔值。"),
            };
        }

        // token：null = 不改；"" = 清除；其它 = 覆盖（加密在服务里做）
        string? token = null;
        if (payload.TryGetProperty("token", out var tokenElement) && tokenElement.ValueKind != JsonValueKind.Null)
        {
            if (tokenElement.ValueKind != JsonValueKind.String)
            {
                throw new ApiException(ApiResponse.BadRequest, "token 必须是字符串。");
            }

            token = tokenElement.GetString()?.Trim() ?? string.Empty;
        }

        IReadOnlyList<string>? allowedSessions = null;
        if (payload.TryGetProperty("allowedSessions", out var sessionsElement))
        {
            if (sessionsElement.ValueKind == JsonValueKind.String)
            {
                allowedSessions = (sessionsElement.GetString() ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (sessionsElement.ValueKind == JsonValueKind.Array)
            {
                allowedSessions = sessionsElement
                    .EnumerateArray()
                    .Select(e => e.GetString()?.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .ToList();
            }
            else
            {
                throw new ApiException(ApiResponse.BadRequest, "allowedSessions 必须是字符串数组。");
            }
        }

        var changed = await _options.UpdateAsync(
            enabled,
            token,
            allowedSessions,
            ReadPositiveInt(payload, "sendTimeoutMs"),
            ReadPositiveInt(payload, "maxTextLength"),
            ReadPositiveInt(payload, "queryTimeoutMs"),
            ct).ConfigureAwait(false);

        if (changed.Count == 0)
        {
            // 值没变不算错：客户端原样再存一次不该报 400（与 setAccess 同口径）
            return new { saved = 0, skipped = true, changed, connected = _channel.IsConnected };
        }

        // 立刻把新参数推给通道：白名单 / 超时对**下一条**生效，不必重启
        _channel.UpdateOptions(_options.Current.ToOptions());

        await _configs
            .AppendAuditAsync("saveDouyin", Actor, string.Join(",", changed), ct)
            .ConfigureAwait(false);

        return new { saved = changed.Count, skipped = false, changed, connected = _channel.IsConnected };
    }

    /// <summary>可选的正整数；给了非正数就当没给（不让 0 把超时变成"立刻超时"）。</summary>
    private static int? ReadPositiveInt(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) && number > 0)
        {
            return number;
        }

        throw new ApiException(ApiResponse.BadRequest, $"{name} 必须是正整数。");
    }
}

/// <summary>
/// 邮件接入的运行时开关（S3-F 补 / 决策七）：客户端点一下就生效，不必重启。
/// </summary>
/// <remarks>
/// payload：<c>{ "enabled": true }</c>。
/// 关掉只是"不再连 IMAP、不再摘要转发"，**已收的邮件与账号配置都保留**。
/// </remarks>
public sealed class SaveMailAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly MailOptionsService _switch;
    private readonly IConfigStore _configs;

    public SaveMailAction(MailOptionsService switchService, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(switchService);
        ArgumentNullException.ThrowIfNull(configs);

        _switch = switchService;
        _configs = configs;
    }

    public string Name => "saveMail";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("enabled", out var enabledElement))
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 enabled（true / false）。");
        }

        var enabled = enabledElement.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ApiException(ApiResponse.BadRequest, "enabled 必须是布尔值。"),
        };

        var changed = await _switch.SetEnabledAsync(enabled, ct).ConfigureAwait(false);
        if (!changed)
        {
            // 值没变不算错（与 setAccess / saveDouyin 同口径）
            return new { enabled = _switch.Enabled, saved = 0, skipped = true };
        }

        await _configs
            .AppendAuditAsync("saveMail", Actor, enabled ? "enabled=1" : "enabled=0", ct)
            .ConfigureAwait(false);

        return new { enabled = _switch.Enabled, saved = 1, skipped = false };
    }
}
