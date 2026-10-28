using ZipperAIQQ.Agent.Llm;

namespace ZipperAIQQ.Server.Configuration;

/// <summary>
/// Server 的全部运行配置。**只从环境变量读取**，代码里不写死任何 URL / 模型名（FR-8.13）。
/// </summary>
/// <remarks>
/// <para>
/// 强类型化是为了让「缺哪项配置」在**启动时一次性说清**，
/// 而不是散在几十个 <c>Environment.GetEnvironmentVariable</c> 里，
/// 等到某个组件构造时才炸。
/// </para>
/// <para>
/// 环境变量 → 字段的映射集中在这一个文件，以后换成配置文件 / 客户端下发，只改这里。
/// </para>
/// </remarks>
public sealed record ServerOptions
{
    // ── 通道 ──
    public required string NapCatWebSocketUrl { get; init; }
    public string? NapCatAccessToken { get; init; }
    public IReadOnlyList<string> Whitelist { get; init; } = [];

    /// <summary>
    /// 语音转写开关（默认开）：走 NapCat 自带的 <c>fetch_ptt_text</c>，不接本地模型（Q5 定稿）。
    /// </summary>
    public bool SttEnabled { get; init; } = true;

    // ── 模型 ──
    public required string LlmApiKey { get; init; }
    public required string LlmBaseUrl { get; init; }
    public required string LlmModel { get; init; }
    public LlmApiKind LlmApiKind { get; init; } = LlmApiKind.Responses;

    // ── 存储与资源 ──
    public string DatabasePath { get; init; } = "./data/zipperaiqq.db";
    public string FacesPath { get; init; } = "./config/faces.qq.json";
    public string EmbeddingModelDirectory { get; init; } = "./temp/bge-small-zh-v1.5";

    /// <summary>全局人设文件（Markdown）。会话级人设可以在运行时覆盖它。</summary>
    public string PersonaPath { get; init; } = "./config/persona.md";

    // ── 权限与工作区（M1-E，FR-6 / FR-7）──

    /// <summary>
    /// 管理员 QQ 列表：**全量文件权限**（全部会话目录）。
    /// </summary>
    /// <remarks>
    /// 默认空 = 没有管理员 = 所有文件工具不可用（deny-by-default）。
    /// </remarks>
    public IReadOnlyList<string> Admins { get; init; } = [];

    /// <summary>
    /// 工具白名单：可读写**自己当前会话**的工作区。
    /// </summary>
    /// <remarks>
    /// ⚠️ 与 <see cref="Whitelist"/>（消息过滤：「它愿意理谁」）**不是一回事** ——
    /// 这里是「谁能让它动手」。默认空。
    /// </remarks>
    public IReadOnlyList<string> ToolWhitelist { get; init; } = [];

    /// <summary>工作区根目录（FR-7.1）：其下按平台 → g/p + 会话 ID 分目录。</summary>
    public string WorkspacePath { get; init; } = "./workspace";

    /// <summary>单次读写大小上限（字节，FR-7.7）：超限拒绝，不静默截断。</summary>
    public int FileMaxBytes { get; init; } = 256 * 1024;

    // ── HTTP / WS 端点（M2-A）──

    /// <summary>监听地址（<c>host:port</c>）。默认只绑 loopback。</summary>
    public string HttpAddress { get; init; } = "127.0.0.1:5180";

    /// <summary>
    /// 鉴权 token：**非 loopback 监听时必须配**，否则拒绝启动（架构 §3.5）。
    /// </summary>
    public string? HttpToken { get; init; }

    /// <summary>
    /// Data Protection 密钥环目录（**主密钥**，FR-8.8）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 它一丢，库里所有 LLM key 都解不开 —— **必须备份**。
    /// 只走引导层（环境变量 / 本地目录），绝不入库。
    /// </remarks>
    public string DataProtectionKeysPath { get; init; } = "./data/keys";

    /// <summary>
    /// 机器人自身账号（mention 模式判断"@ 的是不是自己"）。
    /// </summary>
    /// <remarks>
    /// 留空时：mention 模式只回私聊（群聊里没法判断 @ 指向谁，宁可保守）。
    /// 多平台下这是 `qq` 的单值写法（旧配置），平台化写法见 <see cref="SelfIds"/>。
    /// </remarks>
    public string? SelfId { get; init; }

    /// <summary>
    /// 按平台的自身账号（键 = 平台，如 <c>qq</c> / <c>douyin</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么不能只有一个值（决策三 / S3-A）：QQ 号与抖音 uid 完全无关，
    /// 用 QQ 的 selfId 去判断"抖音这条消息有没有 @ 我"，结果只会是永远判错。
    /// </para>
    /// <para>
    /// 配置形态：YAML 的 <c>selfIds:</c> 节。<c>qq</c> 缺省时回落到旧单值 <see cref="SelfId"/>，
    /// 老配置不需要改（迁移策略：只在读的时候回落，不往文件里写）。
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string> SelfIds { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>取某平台的自身账号；未配置该平台时 `qq` 回落单值，其余返回 <c>null</c>。</summary>
    public string? SelfIdFor(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        if (SelfIds.TryGetValue(platform, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return string.Equals(platform, "qq", StringComparison.OrdinalIgnoreCase) ? SelfId : null;
    }

    /// <summary>网页搜索开关（默认开；关掉时模型看不到 web_search/fetch_url）。</summary>
    public bool SearchEnabled { get; init; } = true;

    /// <summary>搜索服务基地址（默认 https://api.deepseek.com，可用环境变量覆盖）。</summary>
    public string? SearchBaseUrl { get; init; }

    // ── 外部 WS（问题记录 03 #5）──
    /// <summary>外部应用是否允许发消息（默认关）。</summary>
    public bool ExternalAllowSend { get; init; }

    /// <summary>允许外部发送的会话白名单（空 = 全部；支持 <c>qq:p*</c> 这类前缀）。</summary>
    public IReadOnlyList<string> ExternalAllowedSessions { get; init; } = [];

    /// <summary>正向 WS 目标地址（空 = 不连出）。</summary>
    public string? ExternalForwardUrl { get; init; }

    /// <summary>正向 WS 鉴权 token。</summary>
    public string? ExternalForwardToken { get; init; }

    /// <summary>反向 WS 额外监听块（host:port；启动时生效）。</summary>
    public IReadOnlyList<External.ExternalReverseServer> ExternalReverseServers { get; init; } = [];

    // ── 抖音接入（多平台通道接入 · 决策二）──

    /// <summary>
    /// 抖音 sidecar 参数（<c>douyin</c> 配置节）。
    /// </summary>
    /// <remarks>
    /// sidecar 主动连入 <c>/ws/channel</c>；默认 <see cref="Channels.DouyinOptions.Enabled"/> = false，
    /// 关着的时候端点直接 404，服务照常跑（只是多平台里少一个平台）。
    /// </remarks>
    public Channels.DouyinOptions Douyin { get; init; } = new();

    // ── 邮件接入（多平台通道接入 · 决策一 / 九）──

    /// <summary>
    /// 邮件运行参数（<c>mail</c> 配置节）。
    /// </summary>
    /// <remarks>
    /// 账号与凭据**不在这里**：它们存在 <c>MailAccounts</c> 表里（客户端可改），
    /// 这里只放策略参数。默认 <see cref="Mail.MailOptions.Enabled"/> = false（不启用就不连 IMAP）。
    /// </remarks>
    public Mail.MailOptions Mail { get; init; } = new();

    // ── 发送限速 ──
    /// <summary>全局最短发送间隔（毫秒）。</summary>
    public int SendIntervalMs { get; init; } = 300;
    /// <summary>连续快发到多少条后进入冷却。</summary>
    public int MaxConsecutive { get; init; } = 10;

    /// <summary>冷却时长（毫秒）。</summary>
    public int CooldownMs { get; init; } = 10_000;

    /// <summary>上下文热窗保留多少条历史（可被运行参数覆盖）。</summary>
    public int HotWindowMessages { get; init; } = 20;

    // ── 可观测 ──
    public bool OtelConsole { get; init; }

    /// <summary>
    /// 从环境变量构建。**缺必需项直接抛异常**，而不是带着半个配置启动
    /// —— 半个配置跑起来只会让人误以为"服务正常"。
    /// </summary>
    public static ServerOptions FromEnvironment()
    {
        var missing = new List<string>();

        string Require(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(name);
                return string.Empty;
            }
            return value;
        }

        string? Optional(string name) => Environment.GetEnvironmentVariable(name);

        int IntOrDefault(string name, int fallback) =>
            int.TryParse(Optional(name), out var parsed) ? parsed : fallback;

        bool BoolOrDefault(string name, bool fallback) =>
            bool.TryParse(Optional(name), out var parsed) ? parsed : fallback;

        static IReadOnlyList<string> SplitList(string? raw) =>
            (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var wsUrl = Require("ZIPPER_NAPCAT_WS");
        var apiKey = Require("ZIPPER_LLM_APIKEY");
        var baseUrl = Require("ZIPPER_LLM_BASEURL");
        var model = Require("ZIPPER_LLM_MODEL");

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"缺少必需的环境变量：{string.Join(", ", missing)}。" +
                "参考 scripts/run-server.template.ps1 了解完整清单。");
        }

        var apiKindText = Optional("ZIPPER_LLM_APIKIND");

        return new ServerOptions
        {
            NapCatWebSocketUrl = wsUrl,
            NapCatAccessToken = Optional("ZIPPER_NAPCAT_TOKEN"),
            SearchEnabled = BoolOrDefault("ZIPPER_SEARCH_ENABLED", true),
            SearchBaseUrl = Optional("ZIPPER_SEARCH_BASEURL"),
            ExternalAllowSend = BoolOrDefault("ZIPPER_EXTERNAL_ALLOWSEND", false),
            ExternalAllowedSessions = (Optional("ZIPPER_EXTERNAL_ALLOWEDSESSIONS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ExternalForwardUrl = Optional("ZIPPER_EXTERNAL_FORWARDURL"),
            ExternalForwardToken = Optional("ZIPPER_EXTERNAL_FORWARDTOKEN"),
            Whitelist = (Optional("ZIPPER_WHITELIST") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            SttEnabled = Optional("ZIPPER_STT_ENABLED") is not "0",

            LlmApiKey = apiKey,
            LlmBaseUrl = baseUrl,
            LlmModel = model,
            LlmApiKind = string.IsNullOrWhiteSpace(apiKindText)
                ? LlmApiKind.Responses
                : Enum.Parse<LlmApiKind>(apiKindText, ignoreCase: true),

            DatabasePath = Optional("ZIPPER_DB") ?? "./data/zipperaiqq.db",
            FacesPath = Optional("ZIPPER_FACES") ?? "./config/faces.qq.json",
            EmbeddingModelDirectory = Optional("ZIPPER_EMBEDDING_MODEL") ?? "./temp/bge-small-zh-v1.5",
            PersonaPath = Optional("ZIPPER_PERSONA") ?? "./config/persona.md",

            Admins = SplitList(Optional("ZIPPER_ADMINS")),
            ToolWhitelist = SplitList(Optional("ZIPPER_TOOL_WHITELIST")),
            WorkspacePath = Optional("ZIPPER_WORKSPACE") ?? "./workspace",
            FileMaxBytes = IntOrDefault("ZIPPER_FILE_MAX_BYTES", 256 * 1024),

            HttpAddress = Optional("ZIPPER_HTTP_ADDR") ?? "127.0.0.1:5180",
            HttpToken = Optional("ZIPPER_HTTP_TOKEN"),
            DataProtectionKeysPath = Optional("ZIPPER_DP_KEYS") ?? "./data/keys",
            SelfId = Optional("ZIPPER_SELF_ID"),

            SendIntervalMs = IntOrDefault("ZIPPER_SEND_INTERVAL_MS", 300),
            MaxConsecutive = IntOrDefault("ZIPPER_MAX_CONSECUTIVE", 10),
            CooldownMs = IntOrDefault("ZIPPER_COOLDOWN_MS", 10_000),

            OtelConsole = Optional("ZIPPER_OTEL_CONSOLE") == "1",
        };
    }
}
