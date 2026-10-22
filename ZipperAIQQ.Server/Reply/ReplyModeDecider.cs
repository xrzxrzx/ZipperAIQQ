using System.Diagnostics;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Metrics;
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

    private const string JudgePrompt = """
        你是对话判断器。给出一条消息，判断机器人此刻是否应该接话。
        只回答 yes 或 no，不要任何解释。
        判断标准：
        - 被直接提问、话题与机器人相关、接话能自然推进对话 → yes
        - 纯寒暄、与机器人无关的他人闲聊、刷屏式短句 → no
        """;

    private readonly IRuntimeSettingsStore _settings;
    private readonly IConfigStore _configs;
    private readonly ISecretProtector _protector;
    private readonly IMetricsStore _metrics;
    private readonly ILogger _logger;

    // judge 客户端缓存：端点名 + 更新时间变了才重建（避免每次判断都 new HttpClient）
    private IChatClient? _judgeClient;
    private string? _judgeVersion;

    public ReplyModeDecider(
        IRuntimeSettingsStore settings,
        IConfigStore configs,
        ISecretProtector protector,
        IMetricsStore metrics,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _configs = configs;
        _protector = protector;
        _metrics = metrics;
        _logger = logger;
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
            ReplyMode.Jev => await JudgeAsync(message, selfId, ct).ConfigureAwait(false),
            _ => true,
        };
    }

    // ── mention ──

    private bool IsMentioned(IncomingMessage message, string selfId)
    {
        if (string.IsNullOrWhiteSpace(selfId))
        {
            // 没配 self_id：群聊里没法判断 @ 的是不是自己 → 只回私聊（保守）
            return message.Chat.Type == ChatType.Private;
        }

        return message.Segments
            .OfType<MentionSegment>()
            .Any(m => string.Equals(m.UserId, selfId, StringComparison.Ordinal));
    }

    // ── jev ──

    private async Task<bool> JudgeAsync(IncomingMessage message, string selfId, CancellationToken ct)
    {
        var client = await GetJudgeClientAsync(ct).ConfigureAwait(false);
        if (client is null)
        {
            // 11.4：没配 judge 端点 → 降级 mention
            _logger.Warning("jev 模式没有可用的 Judge 端点，降级为 mention");
            return IsMentioned(message, selfId);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(JudgeTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, JudgePrompt),
                    new ChatMessage(ChatRole.User, BuildJudgeContext(message)),
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

    private static string BuildJudgeContext(IncomingMessage message) =>
        $"{message.Chat.Type} | {message.Sender.DisplayName}：{message.PlainText}";

    private async Task<IChatClient?> GetJudgeClientAsync(CancellationToken ct)
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

        var version = $"{endpoint.Name}|{endpoint.UpdatedAt.ToUnixTimeMilliseconds()}";
        if (version == _judgeVersion && _judgeClient is not null)
        {
            return _judgeClient;
        }

        var key = _protector.Unprotect(endpoint.KeyCipher);
        if (string.IsNullOrEmpty(key))
        {
            _logger.Warning("Judge 端点 {Name} 的 key 解不开", endpoint.Name);
            return null;
        }

        try
        {
            _judgeClient = ChatClientFactory.Create(
                new LlmEndpoint(endpoint.Name, endpoint.BaseUrl, endpoint.Model, (LlmApiKind)endpoint.ApiKind),
                key);
            _judgeVersion = version;
            _logger.Information("Judge 客户端已就绪：端点={Name} 模型={Model}", endpoint.Name, endpoint.Model);
            return _judgeClient;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建 Judge 客户端失败（端点 {Name}）", endpoint.Name);
            return null;
        }
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
