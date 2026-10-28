using System.Globalization;
using Serilog;
using ZipperAIQQ.Agent.Llm;
using YamlDotNet.RepresentationModel;

namespace ZipperAIQQ.Server.Configuration;

/// <summary>
/// 从 <c>config/server.yaml</c> 读配置（**优先级高于环境变量**）。
/// </summary>
/// <remarks>
/// <para>
/// 由来：以前所有配置都是环境变量，改个端口要改启动脚本 —— 用户明确要求
/// 「服务端要有一个配置文件存所有配置，最好是 yaml」。
/// </para>
/// <para>
/// <b>优先级</b>：YAML &gt; 环境变量 &gt; 默认值。
/// 之所以让 YAML 压过环境变量：配置文件是"这台机器怎么跑"的正式声明，
/// 而环境变量更多是部署时的临时覆盖手段。两者都在时以文件为准，行为更好预期。
/// 环境变量仍保留为**必需项的兜底**（YAML 没写时用它）。
/// </para>
/// <para>
/// 找不到 YAML 时完全退回环境变量 —— 老部署方式不受影响。
/// </para>
/// </remarks>
public static class ServerConfigLoader
{
    /// <summary>默认配置文件路径（相对工作目录）。</summary>
    public const string DefaultPath = "config/server.yaml";

    public static ServerOptions Load(string? configPath = null, ILogger? logger = null)
    {
        var options = ServerOptions.FromEnvironment();

        var path = configPath
            ?? Environment.GetEnvironmentVariable("ZIPPER_CONFIG")
            ?? DefaultPath;

        if (!File.Exists(path))
        {
            logger?.Information("没有配置文件 {Path}，沿用环境变量", path);
            options.ValidateRequired();
            return options;
        }

        var (merged, loaded) = ReadFromFile(path, options, logger);
        merged.ValidateRequired();

        if (loaded)
        {
            logger?.Information("已加载配置文件 {Path}", path);
        }

        return merged;
    }

    /// <summary>
    /// 读 YAML 并覆盖环境变量；**读失败只告警并回退**（配置写错不该让服务起不来）。
    /// 必需项校验放在调用方 —— 回退后的配置同样要校验。
    /// </summary>
    /// <returns>合并结果，以及「文件是否真的被读入」（日志只在读入时打"已加载"）。</returns>
    private static (ServerOptions Options, bool Loaded) ReadFromFile(
        string path, ServerOptions fallback, ILogger? logger)
    {
        try
        {
            var yaml = new YamlStream();
            using (var reader = new StreamReader(path))
            {
                yaml.Load(reader);
            }

            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            {
                logger?.Warning("配置文件 {Path} 是空的或格式不对，沿用环境变量", path);
                return (fallback, false);
            }

            return (Apply(root, fallback), true);
        }
        catch (Exception ex)
        {
            // 配置写错不该让服务起不来 —— 明确告警后沿用环境变量
            logger?.Warning(ex, "读取配置文件 {Path} 失败，沿用环境变量", path);
            return (fallback, false);
        }
    }

    private static ServerOptions Apply(YamlMappingNode root, ServerOptions o)
    {
        var s = new YamlSection(root);

        return o with
        {
            // ── server ──
            HttpAddress = s.Str("server", "httpAddress", o.HttpAddress)!,
            HttpToken = s.Str("server", "httpToken", o.HttpToken),
            DatabasePath = s.Str("server", "database", o.DatabasePath)!,
            DataProtectionKeysPath = s.Str("server", "dataProtectionKeys", o.DataProtectionKeysPath)!,
            OtelConsole = s.Bool("server", "otelConsole", o.OtelConsole),

            // ── napcat ──
            NapCatWebSocketUrl = s.Str("napcat", "wsUrl", o.NapCatWebSocketUrl)!,
            NapCatAccessToken = s.Str("napcat", "token", o.NapCatAccessToken),
            SelfId = s.Str("napcat", "selfId", o.SelfId),
            // 按平台的自身账号（多平台）：selfIds: { qq: "...", douyin: "..." }
            SelfIds = s.MapSection("selfIds", o.SelfIds),
            SearchEnabled = s.Bool("search", "enabled", o.SearchEnabled),
            SearchBaseUrl = s.Str("search", "baseUrl", o.SearchBaseUrl),
            ExternalAllowSend = s.Bool("external", "allowSend", o.ExternalAllowSend),
            ExternalAllowedSessions = (s.Str("external", "allowedSessions", string.Join(',', o.ExternalAllowedSessions)) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ExternalForwardUrl = s.Str("external", "forwardUrl", o.ExternalForwardUrl),
            ExternalForwardToken = s.Str("external", "forwardToken", o.ExternalForwardToken),

            // ── douyin（多平台通道接入 · 抖音 sidecar）──
            Douyin = o.Douyin with
            {
                Enabled = s.Bool("douyin", "enabled", o.Douyin.Enabled),
                Token = s.Str("douyin", "token", o.Douyin.Token),
                AllowedSessions = (s.Str("douyin", "allowedSessions", string.Join(',', o.Douyin.AllowedSessions)) ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                SendTimeoutMs = s.Int("douyin", "sendTimeoutMs", o.Douyin.SendTimeoutMs),
                MaxTextLength = s.Int("douyin", "maxTextLength", o.Douyin.MaxTextLength),
                QueryTimeoutMs = s.Int("douyin", "queryTimeoutMs", o.Douyin.QueryTimeoutMs),
            },

            // ── llm ──
            LlmApiKey = s.Str("llm", "apiKey", o.LlmApiKey)!,
            LlmBaseUrl = s.Str("llm", "baseUrl", o.LlmBaseUrl)!,
            LlmModel = s.Str("llm", "model", o.LlmModel)!,
            LlmApiKind = s.ApiKind("llm", "apiKind", o.LlmApiKind),

            // ── mail（多平台通道接入 · 邮件）──
            Mail = o.Mail with
            {
                Enabled = s.Bool("mail", "enabled", o.Mail.Enabled),
                InitialDays = s.Int("mail", "initialDays", o.Mail.InitialDays),
                PollIntervalSeconds = s.Int("mail", "pollIntervalSeconds", o.Mail.PollIntervalSeconds),
                MaxBodyChars = s.Int("mail", "maxBodyChars", o.Mail.MaxBodyChars),
                SummaryEnabled = s.Bool("mail", "summaryEnabled", o.Mail.SummaryEnabled),
                MaxConcurrentSummaries = s.Int("mail", "maxConcurrentSummaries", o.Mail.MaxConcurrentSummaries),
                SummaryRetryCount = s.Int("mail", "summaryRetryCount", o.Mail.SummaryRetryCount),
                PersonaDigest = s.Bool("mail", "personaDigest", o.Mail.PersonaDigest),
                DigestMode = s.EnumValue("mail", "digestMode", o.Mail.DigestMode),
                DigestIntervalMinutes = s.Int("mail", "digestIntervalMinutes", o.Mail.DigestIntervalMinutes),
                DailyAtHour = s.Int("mail", "dailyAtHour", o.Mail.DailyAtHour),
                ForwardRetryCount = s.Int("mail", "forwardRetryCount", o.Mail.ForwardRetryCount),
            },

            // ── limits ──
            SendIntervalMs = s.Int("limits", "sendIntervalMs", o.SendIntervalMs),
            MaxConsecutive = s.Int("limits", "maxConsecutive", o.MaxConsecutive),
            CooldownMs = s.Int("limits", "cooldownMs", o.CooldownMs),
            FileMaxBytes = s.Int("limits", "fileMaxBytes", o.FileMaxBytes),
            HotWindowMessages = s.Int("limits", "hotWindowMessages", o.HotWindowMessages),

            // ── access ──
            Admins = s.List("access", "admins", o.Admins),
            ToolWhitelist = s.List("access", "toolWhitelist", o.ToolWhitelist),
            Whitelist = s.List("access", "whitelist", o.Whitelist),

            // ── paths ──
            FacesPath = s.Str("paths", "faces", o.FacesPath)!,
            PersonaPath = s.Str("paths", "persona", o.PersonaPath)!,
            WorkspacePath = s.Str("paths", "workspace", o.WorkspacePath)!,
            EmbeddingModelDirectory = s.Str("paths", "embeddingModel", o.EmbeddingModelDirectory)!,
        };
    }

    /// <summary>YAML 小节读取器：缺项一律回落到传进来的默认值。</summary>
    private readonly struct YamlSection(YamlMappingNode root)
    {
        private YamlNode? Node(string section, string key)
        {
            if (root.Children.TryGetValue(new YamlScalarNode(section), out var sec) &&
                sec is YamlMappingNode map &&
                map.Children.TryGetValue(new YamlScalarNode(key), out var value))
            {
                return value;
            }

            return null;
        }

        public string? Str(string section, string key, string? fallback)
        {
            var node = Node(section, key);
            if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            {
                return fallback;
            }

            var v = scalar.Value.Trim();

            // 支持空字符串表示"清空"（比如 selfId 想留空）
            return v;
        }

        public int Int(string section, string key, int fallback) =>
            Node(section, key) is YamlScalarNode s
            && int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v
                : fallback;

        public bool Bool(string section, string key, bool fallback)
        {
            if (Node(section, key) is not YamlScalarNode s)
            {
                return fallback;
            }

            return s.Value?.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => fallback,
            };
        }

        public IReadOnlyList<string> List(string section, string key, IReadOnlyList<string> fallback)
        {
            if (Node(section, key) is not YamlSequenceNode seq)
            {
                return fallback;
            }

            return seq.Children
                .OfType<YamlScalarNode>()
                .Select(n => n.Value?.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList();
        }

        public LlmApiKind ApiKind(string section, string key, LlmApiKind fallback) =>
            Node(section, key) is YamlScalarNode s
            && Enum.TryParse<LlmApiKind>(s.Value, ignoreCase: true, out var v)
                ? v
                : fallback;

        /// <summary>任意枚举（大小写不敏感）；写错就回落默认值，不让一个拼写错误把服务卡住。</summary>
        public TEnum EnumValue<TEnum>(string section, string key, TEnum fallback)
            where TEnum : struct, Enum =>
            Node(section, key) is YamlScalarNode s
            && Enum.TryParse<TEnum>(s.Value, ignoreCase: true, out var v)
                ? v
                : fallback;

        /// <summary>
        /// 读一整节「标量 → 标量」的映射（如 <c>selfIds:</c>）。空节 / 全空值 → 回落。
        /// </summary>
        /// <remarks>
        /// 与其它成员不同，这一节本身就是 map（不是 section→key→value 的两层查找）。
        /// </remarks>
        public IReadOnlyDictionary<string, string> MapSection(
            string section, IReadOnlyDictionary<string, string> fallback)
        {
            if (!root.Children.TryGetValue(new YamlScalarNode(section), out var node) ||
                node is not YamlMappingNode map)
            {
                return fallback;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (keyNode, valueNode) in map.Children)
            {
                if (keyNode is not YamlScalarNode { Value: { } rawKey } ||
                    valueNode is not YamlScalarNode { Value: { } rawValue })
                {
                    continue;
                }

                var key = rawKey.Trim();
                var value = rawValue.Trim();
                if (key.Length == 0 || value.Length == 0)
                {
                    continue;
                }

                result[key] = value;
            }

            return result.Count > 0 ? result : fallback;
        }
    }
}
