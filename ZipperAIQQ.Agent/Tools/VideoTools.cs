using System.Text;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Agent.Security;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 抖音视频两件套：<c>douyin_video</c>（看画面）/ <c>douyin_transcript</c>（听人声）。
/// </summary>
/// <remarks>
/// <para>
/// **为什么是两个工具而不是一个带开关的**：看画面与听人声的代价完全不同
/// （前者下载 + 解码，后者还要再调一次按秒计费的 ASR），混在一个参数里
/// 模型只会随手把 <c>transcript=true</c> 打开。分开之后，「只看一眼」不会被转写连带。
/// </para>
/// <para>
/// **时间点在这里算**（首帧 / 等分 / 区间）：这是业务语义，而且能单测；
/// 实现（<see cref="IVideoReader"/>）只负责把点变成图。
/// </para>
/// <para>
/// 与其它工具同一套闸：① 不可用或没权限时**连 schema 都不给模型**；
/// ② 每次执行前再过一次门禁（防角色判定被绕过）。
/// </para>
/// <para>
/// 平台归属 <c>douyin</c>：QQ 会话里这条工具连 schema 都不出现 ——
/// 否则模型会拿 QQ 群里的数字去查抖音视频，然后编一个「查不到」。
/// </para>
/// </remarks>
public static class VideoTools
{
    /// <summary>抽帧工具名（与 <c>ToolCatalog</c> 里那一行必须一致）。</summary>
    public const string VideoToolName = "douyin_video";

    /// <summary>转写工具名（与 <c>ToolCatalog</c> 里那一行必须一致）。</summary>
    public const string TranscriptToolName = "douyin_transcript";

    /// <summary>等分抽帧的默认张数（配置上限会再夹一次）。</summary>
    public const int DefaultFrameCount = 6;

    /// <summary>模型给的 count 无论如何不超过这个数（配置层另有上限，这里兜底防止手滑）。</summary>
    public const int HardMaxFrameCount = 16;

    /// <summary>转写文本回填上限（字符）：超出截断并标注，别把上下文吃干。</summary>
    private const int MaxTranscriptChars = 12_000;

    public static IEnumerable<AITool> Create(
        IVideoReader reader,
        ToolCallContext context,
        IPermissionGate gate,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(logger);

        if (reader.CanRead && gate.IsAllowed(ToolCapability.MediaVideo, context.Role))
        {
            yield return CreateVideo(context, gate, reader, logger);
        }

        // 转写**显式**再要求 CanRead：不能指望实现那边用 CanTranscribe 蕴含 CanRead ——
        // 一旦某个实现漏了这个蕴含，底座断开时模型手里会留着一个注定失败的转写工具。
        if (reader.CanRead && reader.CanTranscribe && gate.IsAllowed(ToolCapability.MediaVideo, context.Role))
        {
            yield return CreateTranscript(context, gate, reader, logger);
        }
    }

    // ───────────── 看画面 ─────────────

    private static AIFunction CreateVideo(
        ToolCallContext context,
        IPermissionGate gate,
        IVideoReader reader,
        ILogger logger)
    {
        return AIFunctionFactory.Create(
            async (string reference, string? mode = null, int count = 0, double start = 0, double end = 0,
                CancellationToken ct = default) =>
            {
                if (Deny(gate, context) is { } denied)
                {
                    return [new TextContent(denied)];
                }

                if (!VideoReference.TryParseItemId(reference, out var itemId))
                {
                    return
                    [
                        new TextContent(
                            "看不出要处理哪条视频：请给出作品 id（19 位数字）、视频链接，"
                            + "或直接抄我上一条消息里 `[视频 id=…]` 那一段。")
                    ];
                }

                if (!TryParsePlan(mode, count, start, end, out var plan, out var planError))
                {
                    return [new TextContent(planError)];
                }

                VideoBrief brief;
                try
                {
                    brief = await reader.LocateAsync(itemId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Warning("douyin_video 取详情失败：itemId={ItemId} {Message}", itemId, ex.Message);
                    return [new TextContent($"取这条视频失败：{ex.Message}")];
                }

                // 「等分」与「区间到结尾」都要时长：拿不到就报明白，别按一个瞎猜的长度算
                var times = ResolveTimes(plan, brief.DurationMs, out var timeError);
                if (times is null)
                {
                    return [new TextContent(timeError!)];
                }

                var outcome = await reader.FramesAsync(itemId, times, ct).ConfigureAwait(false);
                if (outcome.Error is { } error)
                {
                    logger.Warning("douyin_video 抽帧失败：itemId={ItemId} {Message}", itemId, error);
                    return [new TextContent($"抽帧失败：{error}")];
                }

                var contents = new List<AIContent>(outcome.Frames.Count + 1)
                {
                    new TextContent(BuildCaption(itemId, brief, outcome.Frames)),
                };

                foreach (var frame in outcome.Frames)
                {
                    contents.Add(new DataContent(frame.Bytes, frame.Mime));
                }

                return contents.ToArray();
            },
            name: VideoToolName,
            description:
                "看抖音视频的画面：按时间点抽帧并把图片给你（首帧 / 等分 N 张 / 指定区间 N 张）。\n" +
                "参数：reference（作品 id、视频链接，或消息里的 `[视频 id=…]`）、" +
                "mode（first 只截首帧 / even 等分（默认）/ range 区间内等分）、" +
                $"count（张数，默认 {DefaultFrameCount}，最多 {HardMaxFrameCount}）、" +
                "start / end（秒，仅 mode=range 用）。\n" +
                "抽的是这条视频本身的画面；每张会标明真实时间点（mm:ss），回答时按那个时间说。");
    }

    // ───────────── 听人声 ─────────────

    private static AIFunction CreateTranscript(
        ToolCallContext context,
        IPermissionGate gate,
        IVideoReader reader,
        ILogger logger)
    {
        return AIFunctionFactory.Create(
            async (string reference, double start = 0, double end = 0, CancellationToken ct = default) =>
            {
                if (Deny(gate, context) is { } denied)
                {
                    return denied;
                }

                if (!VideoReference.TryParseItemId(reference, out var itemId))
                {
                    return "看不出要转写哪条视频：请给出作品 id（19 位数字）、视频链接，"
                           + "或直接抄我上一条消息里 `[视频 id=…]` 那一段。";
                }

                if (start < 0 || end < 0 || (end > 0 && end <= start))
                {
                    return "时间区间不对：start/end 是秒，end 要么留 0（表示到结尾），要么必须大于 start。";
                }

                var fromMs = (long)Math.Round(start * 1000);
                var toMs = (long)Math.Round(end * 1000);

                var outcome = await reader.TranscribeAsync(itemId, fromMs, toMs, ct).ConfigureAwait(false);
                if (outcome.Error is { } error)
                {
                    logger.Warning("douyin_transcript 失败：itemId={ItemId} {Message}", itemId, error);
                    return $"转写失败：{error}";
                }

                return FormatTranscript(itemId, outcome);
            },
            name: TranscriptToolName,
            description:
                "把抖音视频里的人声转成带时间戳的文字（只认人声：纯音乐段落会说「无人声」）。\n" +
                "参数：reference（作品 id / 视频链接 / `[视频 id=…]`）、" +
                "start / end（秒，可只转一小段；都留 0 = 从头开始，长度受服务端上限约束）。\n" +
                "转写走云端 ASR（会按时长计费），只在确实需要「他说了什么」时用；看画面请用 douyin_video。");
    }

    // ───────────── 取点规划（纯逻辑，单测覆盖） ─────────────

    /// <summary>
    /// 解析 <c>mode</c> / <c>count</c> / <c>start</c> / <c>end</c> 成取点计划。
    /// </summary>
    /// <param name="mode">first / even / range（大小写不敏感；空 = even）。</param>
    /// <param name="count">张数（<c>&lt;= 0</c> 用默认值，超过上限按上限）。</param>
    /// <param name="startSeconds">区间起点（秒，仅 range）。</param>
    /// <param name="endSeconds">区间终点（秒，仅 range；<c>0</c> = 到结尾）。</param>
    /// <param name="plan">取点计划。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryParsePlan(
        string? mode,
        int count,
        double startSeconds,
        double endSeconds,
        out FramePlan plan,
        out string error)
    {
        plan = new FramePlan(FramePlanKind.Even, DefaultFrameCount, 0, 0);
        error = string.Empty;

        var normalized = string.IsNullOrWhiteSpace(mode) ? "even" : mode.Trim().ToLowerInvariant();
        var wanted = count <= 0 ? DefaultFrameCount : Math.Min(count, HardMaxFrameCount);

        switch (normalized)
        {
            case "first":
                plan = new FramePlan(FramePlanKind.First, 1, 0, 0);
                return true;

            case "even":
                plan = new FramePlan(FramePlanKind.Even, wanted, 0, 0);
                return true;

            case "range":
                if (startSeconds <= 0 && endSeconds <= 0)
                {
                    error = "mode=range 需要 start / end（秒）：end 留 0 表示到结尾。";
                    return false;
                }

                if (endSeconds > 0 && endSeconds <= startSeconds)
                {
                    error = "区间不对：end 必须大于 start（都用秒）。";
                    return false;
                }

                plan = new FramePlan(
                    FramePlanKind.Range,
                    wanted,
                    (long)Math.Round(Math.Max(0, startSeconds) * 1000),
                    (long)Math.Round(Math.Max(0, endSeconds) * 1000));
                return true;

            default:
                error = $"mode 只支持 first / even / range（收到的是「{mode}」）。";
                return false;
        }
    }

    /// <summary>
    /// 按真实时长把计划落实成毫秒时间点；拿不到时长且必需时返回 <c>null</c> 并给原因。
    /// </summary>
    /// <param name="plan">取点计划。</param>
    /// <param name="durationMs">视频时长（毫秒；<c>0</c> = 不知道）。</param>
    /// <param name="error">失败原因。</param>
    public static List<long>? ResolveTimes(FramePlan plan, long durationMs, out string? error)
    {
        ArgumentNullException.ThrowIfNull(plan);
        error = null;

        switch (plan.Kind)
        {
            case FramePlanKind.First:
                return [0];

            case FramePlanKind.Even:
            {
                if (durationMs <= 0)
                {
                    error = "平台没给这条视频的时长，没法等分；请改用 mode=range 指定秒数，或 mode=first 只看首帧。";
                    return null;
                }

                var n = Math.Max(1, plan.Count);
                var result = new List<long>(n);
                for (var i = 0; i < n; i++)
                {
                    // 第 0 张就是首帧；最后一张落在结尾前一个等分位（不正好贴结尾，那里常是黑屏）
                    result.Add(durationMs * i / n);
                }

                return result;
            }

            case FramePlanKind.Range:
            {
                var from = Math.Max(0, plan.StartMs);
                var to = plan.EndMs;

                if (to <= 0)
                {
                    if (durationMs <= 0)
                    {
                        error = "这条视频的时长拿不到，没法把「到结尾」算成具体秒数；请给出 end。";
                        return null;
                    }

                    to = durationMs;
                }

                if (durationMs > 0 && from >= durationMs)
                {
                    error = $"起点（{FormatTime(from)}）已经超过视频时长（{FormatTime(durationMs)}）。";
                    return null;
                }

                if (durationMs > 0 && to > durationMs)
                {
                    to = durationMs;
                }

                if (to <= from)
                {
                    error = "区间为空：end 必须晚于 start。";
                    return null;
                }

                var n = Math.Max(1, plan.Count);
                if (n == 1)
                {
                    return [(from + to) / 2];
                }

                var points = new List<long>(n);
                for (var i = 0; i < n; i++)
                {
                    points.Add(from + (to - from) * i / (n - 1));
                }

                return points;
            }

            default:
                error = "抽帧时间点规划异常（内部错误）。";
                return null;
        }
    }

    // ───────────── 回填格式 ─────────────

    private static string BuildCaption(string itemId, VideoBrief brief, IReadOnlyList<VideoFrameImage> frames)
    {
        var sb = new StringBuilder();
        sb.Append($"【视频 {itemId}】");
        if (!string.IsNullOrWhiteSpace(brief.Title))
        {
            sb.Append(ShortenTitle(brief.Title, 60));
            sb.Append(' ');
        }

        sb.AppendLine(brief.DurationMs > 0 ? $"（时长 {FormatTime(brief.DurationMs)}）" : "（时长未知）");
        sb.AppendLine($"共 {frames.Count} 张，按顺序给出：");

        foreach (var frame in frames)
        {
            sb.AppendLine($"- 第 {frame.Index + 1} 张：{FormatTime(frame.AtMs)}");
        }

        sb.Append("回答里请用上面这些时间点来说「第几张 / 几分几秒」，不要自己换算出别的时间。");
        return sb.ToString().TrimEnd();
    }

    private static string FormatTranscript(string itemId, VideoTranscriptOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【视频 {itemId}】人声转写（{outcome.Note}）");

        if (string.IsNullOrWhiteSpace(outcome.Text))
        {
            sb.Append("（没有识别到任何人声内容）");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine();
        foreach (var line in outcome.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            sb.AppendLine($"[{FormatTime(line.StartMs)}–{FormatTime(line.EndMs)}] {line.Text.Trim()}");
        }

        var body = sb.ToString().TrimEnd();
        if (body.Length > MaxTranscriptChars)
        {
            body = body[..MaxTranscriptChars] + "\n…（转写过长，已截断；要后面那段请用 start/end 指定区间）";
        }

        return body + "\n（以上是 ASR 原文，没有润色；「无人声」段落就是没识别到说话）";
    }

    /// <summary>
    /// 按**代理对**安全截断（抖音文案里带 emoji，按 code unit 切会留下半个字符 → 进 JSON 成 <c>\uFFFD</c>）。
    /// </summary>
    /// <param name="text">原文。</param>
    /// <param name="maxCodeUnits">最多保留多少个 UTF-16 码元（不含省略号）。</param>
    public static string ShortenTitle(string text, int maxCodeUnits)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (maxCodeUnits <= 0 || text.Length <= maxCodeUnits)
        {
            return text;
        }

        var length = maxCodeUnits;

        // 末位落在高代理上 ⇒ 往前退一格，别把 emoji 切成两半
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length -= 1;
        }

        return length <= 0 ? "…" : string.Concat(text.AsSpan(0, length), "…");
    }

    /// <summary>毫秒 → <c>mm:ss</c> / <c>h:mm:ss</c>（给模型看的时间点）。</summary>
    public static string FormatTime(long milliseconds)
    {
        var totalSeconds = Math.Max(0, milliseconds) / 1000;
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;

        return hours > 0
            ? $"{hours}:{minutes:D2}:{seconds:D2}"
            : $"{minutes:D2}:{seconds:D2}";
    }

    private static string? Deny(IPermissionGate gate, ToolCallContext context)
    {
        var decision = gate.Check(ToolCapability.MediaVideo, context);
        return decision.Allowed ? null : decision.Reason;
    }
}
