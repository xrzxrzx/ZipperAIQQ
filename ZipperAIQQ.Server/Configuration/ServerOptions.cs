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

    // ── 发送限速 ──
    /// <summary>全局最短发送间隔（毫秒）。</summary>
    public int SendIntervalMs { get; init; } = 300;

    /// <summary>连续快发到多少条后进入冷却。</summary>
    public int MaxConsecutive { get; init; } = 10;

    /// <summary>冷却时长（毫秒）。</summary>
    public int CooldownMs { get; init; } = 10_000;

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
            Whitelist = (Optional("ZIPPER_WHITELIST") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),

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

            SendIntervalMs = IntOrDefault("ZIPPER_SEND_INTERVAL_MS", 300),
            MaxConsecutive = IntOrDefault("ZIPPER_MAX_CONSECUTIVE", 10),
            CooldownMs = IntOrDefault("ZIPPER_COOLDOWN_MS", 10_000),

            OtelConsole = Optional("ZIPPER_OTEL_CONSOLE") == "1",
        };
    }
}
