using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZipperAIQQ.Client.Dto;

/// <summary>第三方平台配置总览（<c>getPlatforms</c>，S3-F）。</summary>
/// <remarks>
/// 旧服务端没有这个 action → 调用会抛 <see cref="Services.ApiCallException"/>；
/// 页面必须**降级**（邮件页仍走 <c>listMailAccounts</c>，抖音页提示"需要新服务端"），不能整页白屏。
/// </remarks>
public sealed class PlatformsDto
{
    /// <summary>平台清单（QQ / 抖音…）：页面顶部状态点用。</summary>
    [JsonPropertyName("platforms")]
    public List<PlatformInfoDto> Platforms { get; set; } = [];

    /// <summary>邮件块；**缺失 → null**（不是空对象），页面按"服务端没给"展示。</summary>
    [JsonPropertyName("mail")]
    public MailPlatformDto? Mail { get; set; }

    /// <summary>抖音块；**缺失 → null**。</summary>
    [JsonPropertyName("douyin")]
    public DouyinPlatformDto? Douyin { get; set; }
}

/// <summary>一个平台的连通概况（<c>getPlatforms.platforms[]</c>）。</summary>
public sealed class PlatformInfoDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    /// <summary>平台侧自己的账号 ID（如 QQ 号）；未连上时可能为空。</summary>
    [JsonPropertyName("selfId")]
    public string? SelfId { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>该平台支持的能力名；旧服务端没给 → 空表。</summary>
    [JsonPropertyName("features")]
    public List<string> Features { get; set; } = [];
}

/// <summary>邮件平台块（<c>getPlatforms.mail</c>）。</summary>
public sealed class MailPlatformDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>汇总是否再交人设 agent 润色（true = 用人设语气转告，false = 直接发汇总文本）。</summary>
    [JsonPropertyName("personaDigest")]
    public bool PersonaDigest { get; set; }

    [JsonPropertyName("accounts")]
    public List<MailAccountDto> Accounts { get; set; } = [];
}

/// <summary>
/// 一个收信账号（<c>listMailAccounts</c> 数组项 / <c>saveMailAccount</c> 回显）。
/// </summary>
/// <remarks>
/// ⚠️ **契约里没有明文、也没有密文**：只有 <see cref="HasSecret"/>。
/// 要换授权码只能整条重填（<c>secret</c> 缺省 = 保留旧密文）。
/// </remarks>
public sealed class MailAccountDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("imapHost")]
    public string ImapHost { get; set; } = string.Empty;

    [JsonPropertyName("imapPort")]
    public int ImapPort { get; set; } = 993;

    [JsonPropertyName("useSsl")]
    public bool UseSsl { get; set; } = true;

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>最近一次同步（Unix 毫秒）；**没同步过是 null**，别当 0 显示成 1970。</summary>
    [JsonPropertyName("lastSyncAt")]
    public long? LastSyncAt { get; set; }

    /// <summary>上次同步失败的原因；正常时 null。</summary>
    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    /// <summary>是否已存授权码（**只有布尔，永不放凭据**）。</summary>
    [JsonPropertyName("hasSecret")]
    public bool HasSecret { get; set; }
}

/// <summary>抖音平台块（<c>getPlatforms.douyin</c>）。**不含 token 明文**，只给 <see cref="TokenConfigured"/>。</summary>
public sealed class DouyinPlatformDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("selfId")]
    public string? SelfId { get; set; }

    [JsonPropertyName("tokenConfigured")]
    public bool TokenConfigured { get; set; }

    /// <summary>出站 ack 超时（毫秒）；服务端默认 15000。</summary>
    [JsonPropertyName("sendTimeoutMs")]
    public int SendTimeoutMs { get; set; } = 15_000;

    /// <summary>文本分段阈值（字符）；服务端默认 16000。</summary>
    [JsonPropertyName("maxTextLength")]
    public int MaxTextLength { get; set; } = 16_000;

    /// <summary>联系人 / 群查询超时（毫秒）；服务端默认 10000。</summary>
    [JsonPropertyName("queryTimeoutMs")]
    public int QueryTimeoutMs { get; set; } = 10_000;

    /// <summary>sidecar 连入的端点（<c>/ws/channel</c>）。</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = Services.ClientDefaults.DouyinEndpoint;

    /// <summary>
    /// 登录态：<c>online</c> / <c>waitingQr</c> / <c>loggedOut</c> / <c>error</c> / <c>unknown</c>。
    /// **旧服务端不返回 → 空串**，客户端据此把「登录 / 退出」按钮禁用（不假装支持）。
    /// </summary>
    [JsonPropertyName("loginState")]
    public string LoginState { get; set; } = string.Empty;
}

/// <summary>抖音登录态详情（<c>getDouyinLogin</c> / <c>startDouyinLogin</c>）。</summary>
public sealed class DouyinLoginDto
{
    /// <summary>online / waitingQr / loggedOut / error / unknown。</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "unknown";

    [JsonPropertyName("selfId")]
    public string? SelfId { get; set; }

    /// <summary>登录二维码（**PNG 的 base64，无 <c>data:</c> 前缀**）；没有时 null。</summary>
    [JsonPropertyName("qrcodeBase64")]
    public string? QrcodeBase64 { get; set; }

    /// <summary>
    /// 二维码生成时间（Unix 毫秒，UTC）；**没有二维码时服务端给 null**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 必须是 <c>long?</c>：写成不可空的 <c>long</c> 时，服务端返回 <c>"qrcodeAt":null</c>
    /// 会让 JsonSerializer 抛异常，界面报「服务端返回的不是契约 JSON」——
    /// 与 saveDouyin 的 <c>saved</c> 是同一类事故（2026-10-28 已踩两次）。
    /// </remarks>
    [JsonPropertyName("qrcodeAt")]
    public long? QrcodeAt { get; set; }

    /// <summary>出错时的原因（<c>state = error</c> 才有）；其它状态通常 null。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>退出抖音登录的结果（<c>logoutDouyin</c>）。</summary>
/// <remarks>只退出**登录态**，sidecar 进程继续跑（不会被本操作杀掉）。</remarks>
public sealed class DouyinLogoutResultDto
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "loggedOut";

    [JsonPropertyName("loggedOut")]
    public bool LoggedOut { get; set; }
}

/// <summary>保存抖音配置的结果（<c>saveDouyin</c>）。</summary>
public sealed class SaveDouyinResultDto
{
    /// <summary>真正改了几个字段（**数字**，不是 bool）。</summary>
    /// <remarks>
    /// ⚠️ 这里曾经写成 <c>bool</c>，而服务端返回的是计数（<c>"saved":2</c>）→
    /// JsonSerializer 直接抛异常，界面报「服务端返回的不是契约 JSON」，抖音配置怎么都存不上
    /// （用户实测 2026-10-28）。以后改这个契约**两边一起改**。
    /// </remarks>
    [JsonPropertyName("saved")]
    public int Saved { get; set; }

    /// <summary>没有任何字段变化（此时 <see cref="Saved"/> = 0）。</summary>
    [JsonPropertyName("skipped")]
    public bool Skipped { get; set; }

    /// <summary>真正变了的字段名；没变化 → 空表。</summary>
    [JsonPropertyName("changed")]
    public List<string> Changed { get; set; } = [];

    /// <summary>保存后 sidecar 当前是否连着（配置改完不一定立刻重连）。</summary>
    [JsonPropertyName("connected")]
    public bool Connected { get; set; }
}

/// <summary>保存邮件配置的结果（<c>saveMail</c>：启用开关 + 人设润色开关，都是运行时热切）。</summary>
/// <remarks>
/// 值没变时服务端回 <c>saved = 0 / skipped = true</c>，**不算错**；
/// <see cref="Enabled"/> / <see cref="PersonaDigest"/> 是落库后的真值 —— 界面以它们为准，别拿自己拨的值当结果。
/// </remarks>
public sealed class SaveMailResultDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>人设润色的真值（与启用开关同一次响应回显）。</summary>
    [JsonPropertyName("personaDigest")]
    public bool PersonaDigest { get; set; }

    /// <summary>实际改动的键数：0 = 本来就是这值（配合 <see cref="Skipped"/>）。</summary>
    [JsonPropertyName("saved")]
    public int Saved { get; set; }

    [JsonPropertyName("skipped")]
    public bool Skipped { get; set; }
}
