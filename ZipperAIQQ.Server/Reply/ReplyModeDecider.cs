using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;
using ZipperAIQQ.Storage.Personas;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Reply;

/// <summary>
/// 回复触发模式判定（FR-11）：全局默认 + 每聊天覆盖；<c>jev</c> 失败降级 <c>mention</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>降级是硬要求（11.4）</b>：判断模型不可用 / 超时时回落 <c>mention</c> ——
/// 不丢消息、不阻塞。降级不是异常路径，是设计的一部分。
/// </para>
/// <para>
/// <b>判断要留痕（11.5）</b>：结果与耗时都写日志，耗时进指标（<c>jev.judge</c>）。
/// </para>
/// </remarks>
public sealed class ReplyModeDecider : IReplyModeDecider
{
    private const string DefaultKey = "replymode.default";
    private const string SessionKeyPrefix = "replymode:";

    private static readonly TimeSpan JudgeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>喂给判断器的历史消息条数（含当前这条）。</summary>
    /// <remarks>
    /// 只给一条消息看不出"别人正在聊什么"，判断器会把任何一句话都当成可以插嘴的时机；
    /// 给最近十几条，它才能判断「是不是在插别人话」。
    /// </remarks>
    private const int JudgeHistoryCount = 12;

    private const string JudgePrompt = """
        你是「该不该插话」的判断器。给你机器人的**人设**和一段**最近的聊天记录**，
        判断机器人此刻是否应该说话。

        只回答 yes 或 no，不要任何解释。

        判断原则：
        - 被直接提问、被叫到名字、话题与机器人明确相关 → yes
        - 别人之间正在聊与机器人无关的事 → **no**（硬插话很讨人厌）
        - 人设偏冷淡/高冷 → 更倾向 no，只在被明确叫到或话题高度相关时 yes
        - 人设偏活泼/黏人 → 可以更主动
        - 纯刷屏短句、无意义表情、重复内容 → no
        - 拿不准时倾向 no（少说一句比说错一句好）
        """;

    private readonly IRuntimeSettingsStore _settings;
    private readonly IConfigStore _configs;
    private readonly ISecretProtector _protector;
    private readonly IMetricsStore _metrics;
    private readonly IMessageStore _messages;
    private readonly IPersonaStore _personas;
    private readonly DecisionsJudgeClient _decisions;
    private readonly ILogger _logger;

    // judge 客户端缓存：端点名 + 更新时间变了才重建（避免每次判断都 new HttpClient）
    private IChatClient? _judgeClient;
    private string? _judgeVersion;

    public ReplyModeDecider(
        IRuntimeSettingsStore settings,
        IConfigStore configs,
        ISecretProtector protector,
        IMetricsStore metrics,
        IMessageStore messages,
        IPersonaStore personas,
        DecisionsJudgeClient decisions,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(personas);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _configs = configs;
        _protector = protector;
        _metrics = metrics;
        _messages = messages;
        _personas = personas;
        _logger = logger;

        _decisions = decisions;
    }

    public async Task<ReplyMode> GetModeAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var perSession = await _settings.GetAsync(SessionKeyPrefix + sessionId, ct).ConfigureAwait(false);
        if (TryParse(perSession, out var mode))
        {
            return mode;
        }

        var global = await _settings.GetAsync(DefaultKey, ct).ConfigureAwait(false);
        return TryParse(global, out var fallback) ? fallback : ReplyMode.Every;
    }

    public async Task<bool> ShouldReplyAsync(IncomingMessage message, string selfId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var mode = await GetModeAsync(message.Chat.SessionId, ct).ConfigureAwait(false);

        return mode switch
        {
            ReplyMode.Every => true,
            ReplyMode.Mention => IsMentioned(message, selfId),
            // JEV 模式语义：
            //   · **私聊** → 必回（对方就是在专门跟我说话，判断"该不该"没有意义）
            //   · 群里**被 @** → 必回，不消耗一次判断
            //   · 群里没 @ → 交给 JEV 判断「该不该插话」
            ReplyMode.Jev => message.Chat.Type == ChatType.Private
                             || IsMentioned(message, selfId)
                             || await JudgeAsync(message, selfId, ct).ConfigureAwait(false),
            _ => true,
        };
    }

    // ── mention ──

    public bool IsMentioned(IncomingMessage message, string selfId)
    {
        // 私聊**永远**算「对我说」——私聊里根本没有 @ 这个概念。
        // ⚠️ 这里曾经写成「配了 selfId 就一律要求 @」，导致配了 ZIPPER_SELF_ID 之后
        //    私聊完全不回复（mention 模式与 jev 降级都走这条路）——实测踩到过。
        if (message.Chat.Type == ChatType.Private)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(selfId))
        {
            // 群聊 + 没配 selfId：判断不了 @ 的是不是自己 → 保守视为「没 @ 到」
            return false;
        }

        return message.Segments
            .OfType<MentionSegment>()
            .Any(m => string.Equals(m.UserId, selfId, StringComparison.Ordinal));
    }

    // ── jev ──

    private async Task<bool> JudgeAsync(IncomingMessage message, string selfId, CancellationToken ct)
    {
        var judge = await GetJudgeAsync(ct).ConfigureAwait(false);
        if (judge is null)
        {
            // 11.4：没配 judge 端点 → 降级 mention
            _logger.Warning("jev 模式没有可用的 Judge 端点，降级为 mention");
            return IsMentioned(message, selfId);
        }

        // decisions 模型（Jev 这类）走专用接口：它不接受 chat/completions
        if (judge.Value.Endpoint.ApiKind == LlmApiKind.Decisions)
        {
            return await JudgeWithDecisionsAsync(judge.Value, message, ct).ConfigureAwait(false);
        }

        var client = judge.Value.Client;
        if (client is null)
        {
            _logger.Warning("judge 端点 {Name} 没有可用的 chat 客户端，降级为 mention", judge.Value.Endpoint.Name);
            return IsMentioned(message, selfId);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(JudgeTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            var context = await BuildJudgeContextAsync(message, ct).ConfigureAwait(false);

            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, JudgePrompt),
                    new ChatMessage(ChatRole.User, context),
                ],
                cancellationToken: cts.Token).ConfigureAwait(false);

            sw.Stop();

            var text = (response.Text ?? string.Empty).Trim().ToLowerInvariant();
            var shouldReply = text.StartsWith("yes", StringComparison.Ordinal) || text.StartsWith("是", StringComparison.Ordinal);

            await RecordAsync(message.Chat.SessionId, sw.Elapsed.TotalMilliseconds, shouldReply, ct).ConfigureAwait(false);
            _logger.Information("JEV 判断：{Result}（{Elapsed:F0}ms，会话 {SessionId}）",
                shouldReply ? "回" : "不回", sw.Elapsed.TotalMilliseconds, message.Chat.SessionId);

            return shouldReply;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // 超时 / 网络 / 模型报错：一律降级，不丢消息
            _logger.Warning(ex, "JEV 判断失败，降级为 mention（会话 {SessionId}）", message.Chat.SessionId);
            await RecordAsync(message.Chat.SessionId, sw.Elapsed.TotalMilliseconds, false, ct).ConfigureAwait(false);
            await RecordDegradeAsync(message.Chat.SessionId, ct).ConfigureAwait(false);
            return IsMentioned(message, selfId);
        }
    }

    /// <summary>
    /// 组判断上下文：**人设** + 最近十几条消息 + 当前这条。
    /// </summary>
    /// <remarks>
    /// 人设要参与判断：冷淡的角色本来就该少说话，
    /// 用同一套"是否该回"的标准去衡量所有人设，结果一定是话痨。
    /// </remarks>
    private async Task<string> BuildJudgeContextAsync(IncomingMessage message, CancellationToken ct)
    {
        var sb = new StringBuilder();

        try
        {
            var persona = await _personas
                .GetAsync(message.Chat.Platform, message.Chat.Type.ToString(), message.Chat.Id, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(persona.Content))
            {
                // 只取开头一段：判断器只需要"性格倾向"，不需要完整设定（省 token）
                var excerpt = persona.Content.Length > 400
                    ? persona.Content[..400] + "…"
                    : persona.Content;

                sb.AppendLine("【机器人人设】");
                sb.AppendLine(excerpt);
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "取人设失败，判断器不带人设");
        }

        try
        {
            var recent = await _messages
                .GetRecentAsync(message.Chat.SessionId, JudgeHistoryCount, ct)
                .ConfigureAwait(false);

            // 最近一条就是当前消息（已落库），单独列，避免重复
            var history = recent
                .OrderBy(m => m.Id)
                .Where(m => !string.Equals(m.Message.PlainText, message.PlainText, StringComparison.Ordinal))
                .TakeLast(JudgeHistoryCount - 1)
                .ToList();

            if (history.Count > 0)
            {
                sb.AppendLine("【最近的聊天】");
                foreach (var stored in history)
                {
                    var who = stored.Message.Sender.DisplayName;
                    var what = stored.Message.PlainText?.Trim();
                    if (string.IsNullOrEmpty(what))
                    {
                        continue;
                    }

                    sb.AppendLine($"{who}：{what}");
                }

                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "取最近消息失败，判断器只看当前消息");
        }

        sb.AppendLine("【当前这条】");
        sb.AppendLine($"{message.Sender.DisplayName}：{message.PlainText}");
        sb.AppendLine();
        sb.Append("机器人此刻该说话吗？（只答 yes 或 no）");

        return sb.ToString();
    }

    /// <summary>judge 端点的解析结果（端点 + 明文 key + 可选的 chat 客户端）。</summary>
    private readonly record struct JudgeBinding(LlmEndpoint Endpoint, string ApiKey, IChatClient? Client);

    private async Task<JudgeBinding?> GetJudgeAsync(CancellationToken ct)
    {
        var roles = await _configs.ListRolesAsync(ct).ConfigureAwait(false);
        var judgeRole = roles.FirstOrDefault(r => r.Role.Equals("Judge", StringComparison.OrdinalIgnoreCase));
        if (judgeRole is null)
        {
            return null;
        }

        var endpoint = await _configs.GetEndpointAsync(judgeRole.EndpointName, ct).ConfigureAwait(false);
        if (endpoint is null)
        {
            return null;
        }

        var key = _protector.Unprotect(endpoint.KeyCipher);
        if (string.IsNullOrEmpty(key))
        {
            _logger.Warning("Judge 端点 {Name} 的 key 解不开", endpoint.Name);
            return null;
        }

        var llm = new LlmEndpoint(endpoint.Name, endpoint.BaseUrl, endpoint.Model, (LlmApiKind)endpoint.ApiKind);

        // decisions 模型（Jev）不走 chat 客户端：交给 DecisionsJudgeClient
        if (llm.ApiKind == LlmApiKind.Decisions)
        {
            _logger.Debug("Judge 端点 {Name} 是 decisions 模型（{Model}）", endpoint.Name, endpoint.Model);
            return new JudgeBinding(llm, key, null);
        }

        var version = $"{endpoint.Name}|{endpoint.UpdatedAt.ToUnixTimeMilliseconds()}";
        if (version == _judgeVersion && _judgeClient is not null)
        {
            return new JudgeBinding(llm, key, _judgeClient);
        }

        try
        {
            _judgeClient = ChatClientFactory.Create(llm, key);
            _judgeVersion = version;
            _logger.Information("Judge 客户端已就绪：端点={Name} 模型={Model} 形态={Kind}",
                endpoint.Name, endpoint.Model, llm.ApiKind);
            return new JudgeBinding(llm, key, _judgeClient);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建 Judge 客户端失败（端点 {Name}）", endpoint.Name);
            return null;
        }
    }

    /// <summary>用 Decisions API（Jev）判断该不该回。</summary>
    private async Task<bool> JudgeWithDecisionsAsync(JudgeBinding judge, IncomingMessage message, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(JudgeTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            var (personaText, recent) = await BuildDecisionsContextAsync(message, ct).ConfigureAwait(false);

            var threshold = await GetJudgeThresholdAsync(ct).ConfigureAwait(false);

            var (shouldReply, probability) = await _decisions
                .JudgeAsync(judge.Endpoint, judge.ApiKey, personaText, recent, message.PlainText, threshold, cts.Token)
                .ConfigureAwait(false);

            sw.Stop();
            await RecordAsync(message.Chat.SessionId, sw.Elapsed.TotalMilliseconds, shouldReply, ct).ConfigureAwait(false);

            _logger.Information("JEV(decisions) 判断：{Result}（概率 {P:F2}，{Elapsed:F0}ms，会话 {SessionId}）",
                shouldReply ? "回" : "不回", probability, sw.Elapsed.TotalMilliseconds, message.Chat.SessionId);

            return shouldReply;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.Warning(ex, "JEV(decisions) 判断失败，降级为 mention（会话 {SessionId}）", message.Chat.SessionId);
            await RecordDegradeAsync(message.Chat.SessionId, ct).ConfigureAwait(false);
            return IsMentioned(message, string.Empty);
        }
    }

    /// <summary>判断灵敏度：运行时配置项 <c>jev.threshold</c>，默认 0.55。</summary>
    /// <remarks>调低更爱插话（漏回代价高时用），调高更少出声（误回代价高时用）。</remarks>
    private async Task<double> GetJudgeThresholdAsync(CancellationToken ct)
    {
        try
        {
            var raw = await _settings.GetAsync(NumericSettingsSchema.JudgeThresholdKey, ct).ConfigureAwait(false);
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                return Math.Clamp(v, 0.05, 0.95);
            }
        }
        catch
        {
            // 读不到就用默认
        }

        return 0.55;
    }

    /// <summary>decisions 用的上下文：人设 + 最近几条对话。</summary>
    private async Task<(string? Persona, IReadOnlyList<string> Recent)> BuildDecisionsContextAsync(
        IncomingMessage message,
        CancellationToken ct)
    {
        var persona = await _personas
            .GetAsync(message.Chat.Platform, message.Chat.Type.ToString(), message.Chat.Id, ct)
            .ConfigureAwait(false);

        var recent = new List<string>();
        try
        {
            var history = await _messages
                .GetRecentAsync(message.Chat.SessionId, 6, ct)
                .ConfigureAwait(false);

            foreach (var m in history)
            {
                var who = m.IsOutgoing ? "我" : "对方";
                var txt = m.Message.PlainText.Replace('\n', ' ').Trim();
                if (txt.Length == 0)
                {
                    continue;
                }

                recent.Add($"{who}：{(txt.Length > 80 ? txt[..80] + "…" : txt)}");
            }
        }
        catch
        {
            // 历史取不到不影响判断
        }

        return (persona.Content, recent);
    }

    /// <summary>
    /// 记一次判断：<c>InputTokens</c> 存 <b>1 = 该回 / 0 = 不回</b>，
    /// 聚合时 sum 就是"回了多少次"，count - sum 就是"忍住多少次"。
    /// </summary>
    private async Task RecordAsync(string sessionId, double elapsedMs, bool shouldReply, CancellationToken ct)
    {
        try
        {
            await _metrics
                .RecordAsync(new MetricEntry(sessionId, "jev.judge", elapsedMs, shouldReply ? 1 : 0), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // 指标失败不影响判断
        }
    }

    /// <summary>降级单独记一类，免得和"判断为不回"混在一起。</summary>
    private async Task RecordDegradeAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await _metrics
                .RecordAsync(new MetricEntry(sessionId, "jev.degrade", 0), ct)
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static bool TryParse(string? text, out ReplyMode mode)
    {
        mode = ReplyMode.Every;
        return !string.IsNullOrWhiteSpace(text) &&
               Enum.TryParse(text.Trim(), ignoreCase: true, out mode);
    }
}
