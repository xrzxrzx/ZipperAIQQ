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

            SendIntervalMs = IntOrDefault("ZIPPER_SEND_INTERVAL_MS", 300),
            MaxConsecutive = IntOrDefault("ZIPPER_MAX_CONSECUTIVE", 10),
            CooldownMs = IntOrDefault("ZIPPER_COOLDOWN_MS", 10_000),

            OtelConsole = Optional("ZIPPER_OTEL_CONSOLE") == "1",
        };
    }
}
