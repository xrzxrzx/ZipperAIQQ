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
                // 「汇总再经人格润色」的可选项（2026-10-28 用户要求）
                personaDigest = _mailSwitch.PersonaDigest,
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
                // 会话白名单已并入「人员与名单」（access.douyin.whitelist），这里不再回传
                sendTimeoutMs = douyin.SendTimeoutMs,
                maxTextLength = douyin.MaxTextLength,
                queryTimeoutMs = douyin.QueryTimeoutMs,
                // sidecar 要连的端点：给客户端显示，便于用户对着配 sidecar
                endpoint = "/ws/channel",
                // 登录管理（2026-10-28）：online / waitingQr / loggedOut / error / unknown
                loginState = _douyin.LoginState,
                loginMessage = _douyin.LoginMessage,
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

        // 会话白名单不在这里改（2026-10-28）：统一走 setAccess 的 access.douyin.whitelist，
        // 配置入口在客户端「人员与名单」。老的 allowedSessions 入参**忽略**（不报错，兼容旧客户端）。
        var changed = await _options.UpdateAsync(
            enabled,
            token,
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
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 必须是对象。");
        }

        // 两个开关都可选：只传要改的那个（客户端拨哪个就传哪个）
        var enabled = ReadOptionalBool(payload, "enabled");
        var personaDigest = ReadOptionalBool(payload, "personaDigest");

        if (enabled is null && personaDigest is null)
        {
            throw new ApiException(ApiResponse.BadRequest,
                "payload 需要 enabled 或 personaDigest（布尔值）。");
        }

        var changed = await _switch.SetAsync(enabled, personaDigest, ct).ConfigureAwait(false);

        if (changed.Count > 0)
        {
            await _configs
                .AppendAuditAsync("saveMail", Actor, string.Join(",", changed), ct)
                .ConfigureAwait(false);
        }

        // 值没变时 saved=0 + skipped=true（不算错，与 setAccess / saveDouyin 同口径）
        return new
        {
            enabled = _switch.Enabled,
            personaDigest = _switch.PersonaDigest,
            saved = changed.Count,
            skipped = changed.Count == 0,
            changed,
        };
    }

    /// <summary>可选布尔字段：缺失 / null → <c>null</c>（= 不改）；给了非布尔 → 400。</summary>
    private static bool? ReadOptionalBool(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ApiException(ApiResponse.BadRequest, $"{name} 必须是布尔值。"),
        };
    }
}

/// <summary>抖音登录状态（<c>getDouyinLogin</c>）：状态 + 二维码 + selfId。</summary>
/// <remarks>
/// 客户端用轮询拿它：点「登录」→ 每 2 秒取一次 → 二维码出来就显示 → <c>online</c> 就停。
/// 二维码只放内存，进程重启就没了（重新登录会再生成一张），不落库。
/// </remarks>
public sealed class GetDouyinLoginAction : IApiAction
{
    private readonly DouyinChannel _channel;

    public GetDouyinLoginAction(DouyinChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
    }

    public string Name => "getDouyinLogin";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct) =>
        Task.FromResult<object?>(new
        {
            state = _channel.LoginState,
            selfId = _channel.SelfId,
            qrcodeBase64 = _channel.LoginQrBase64,
            qrcodeAt = _channel.LoginQrAt?.ToUnixTimeMilliseconds(),
            message = _channel.LoginMessage,
        });
}

/// <summary>让 sidecar 发起抖音登录（<c>startDouyinLogin</c>）。</summary>
public sealed class StartDouyinLoginAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly DouyinChannel _channel;
    private readonly IConfigStore _configs;

    public StartDouyinLoginAction(DouyinChannel channel, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(configs);

        _channel = channel;
        _configs = configs;
    }

    public string Name => "startDouyinLogin";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (!_channel.IsConnected)
        {
            throw new ApiException(ApiResponse.BadRequest,
                "sidecar 还没连上来，无法发起登录；先把 sidecar 起起来（本页上方能看到状态）。");
        }

        await _channel.RequestLoginAsync(ct).ConfigureAwait(false);
        await _configs.AppendAuditAsync("startDouyinLogin", Actor, "start", ct).ConfigureAwait(false);

        return new
        {
            state = _channel.LoginState,
            selfId = _channel.SelfId,
            qrcodeBase64 = _channel.LoginQrBase64,
            qrcodeAt = _channel.LoginQrAt?.ToUnixTimeMilliseconds(),
            message = _channel.LoginMessage,
        };
    }
}

/// <summary>退出抖音登录（<c>logoutDouyin</c>）：只清登录态，sidecar 继续跑。</summary>
public sealed class LogoutDouyinAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly DouyinChannel _channel;
    private readonly IConfigStore _configs;

    public LogoutDouyinAction(DouyinChannel channel, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(configs);

        _channel = channel;
        _configs = configs;
    }

    public string Name => "logoutDouyin";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (!_channel.IsConnected)
        {
            throw new ApiException(ApiResponse.BadRequest,
                "sidecar 没连上来，无处下发退出命令；请先确认 sidecar 在运行。");
        }

        await _channel.LogoutAsync(ct).ConfigureAwait(false);
        await _configs.AppendAuditAsync("logoutDouyin", Actor, "logout", ct).ConfigureAwait(false);

        return new { state = _channel.LoginState, loggedOut = true };
    }
}
