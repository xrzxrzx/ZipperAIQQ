using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Mail;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 邮件双通道工具：<c>mail_render</c>（看画面）/ <c>mail_raw</c>（读原文）。
/// </summary>
/// <remarks>
/// <para>
/// **两条通道是两个工具**（用户明确要求不可合并）：关掉渲染不影响读原文，
/// 反过来也一样；模型也能据此表达"我只要看版式"或"我要源头"。
/// </para>
/// <para>
/// 与文件 / 媒体工具同一套闸：① 不可用或没权限的工具**连 schema 都不给模型**；
/// ② 每次执行前再过一次门禁（防角色判定被绕过）。
/// </para>
/// <para>
/// **为什么两者的能力码不同**（<see cref="ToolCapability.MailRead"/> /
/// <see cref="ToolCapability.MailRaw"/>）：渲染给的是"版面印象"，原文给的是
/// "验证码、账单号、Received 链"这种可被直接利用的东西 —— 后者必须更严，
/// 合并成一个码就等于把严的那道拆掉。
/// </para>
/// <para>
/// 原文每次调用都**留审计**（<c>IConfigStore.AppendAuditAsync</c>）：谁在什么时候读了哪封邮件，
/// 是这类能力唯一可追溯的底线。渲染也留一条（同样是把邮件内容递给模型）。
/// </para>
/// </remarks>
public static class MailTools
{
    public const string RenderToolName = "mail_render";
    public const string RawToolName = "mail_raw";

    /// <summary>定位时最多给几个候选（多了等于把邮件列表倒进上下文）。</summary>
    private const int MaxCandidates = 5;

    private const string Actor = "tool:mail";

    public static IEnumerable<AITool> Create(
        ToolCallContext context,
        IPermissionGate gate,
        IMailReader reader,
        IConfigStore configs,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(logger);

        if (reader.CanRender && gate.IsAllowed(ToolCapability.MailRead, context.Role))
        {
            yield return CreateRender(context, gate, reader, configs, logger);
        }

        if (gate.IsAllowed(ToolCapability.MailRaw, context.Role))
        {
            yield return CreateRaw(context, gate, reader, configs, logger);
        }
    }

    // ───────────── 渲染（通道 A） ─────────────

    private static AIFunction CreateRender(
        ToolCallContext context,
        IPermissionGate gate,
        IMailReader reader,
        IConfigStore configs,
        ILogger logger)
    {
        return AIFunctionFactory.Create(
            async (string mail, bool segments = false, CancellationToken ct = default) =>
            {
                if (Deny(gate, ToolCapability.MailRead, context) is { } denied)
                {
                    return [new TextContent(denied)];
                }

                var located = await LocateAsync(reader, mail, ct).ConfigureAwait(false);
                if (located.Problem is { } problem)
                {
                    return [new TextContent(problem)];
                }

                var picked = located.Mail!;

                try
                {
                    var outcome = await reader.RenderAsync(picked.Id, segments, ct).ConfigureAwait(false);
                    if (outcome.Error is { } error)
                    {
                        logger.Warning(
                            "mail_render 失败：mailId={MailId} 原因={Error}", picked.Id, error);
                        return [new TextContent(error)];
                    }

                    await configs
                        .AppendAuditAsync(
                            "mailRender",
                            Actor,
                            $"id={picked.Id} 张数={outcome.Images.Count} segments={segments}",
                            ct)
                        .ConfigureAwait(false);

                    var contents = new List<AIContent>(outcome.Images.Count + 1)
                    {
                        new TextContent(BuildRenderCaption(picked, outcome)),
                    };

                    foreach (var image in outcome.Images)
                    {
                        contents.Add(new DataContent(image.Bytes, image.Mime));
                    }

                    return contents.ToArray();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "mail_render 未预期失败：mailId={MailId}", picked.Id);
                    return [new TextContent($"渲染失败：{ex.Message}（如实告诉对方这次没做成，别猜版面）")];
                }
            },
            name: RenderToolName,
            description:
                "把一封邮件**渲染成画面**（截图），用来看版式：表格、按钮、图片、隐藏的小字。\n" +
                "参数：mail（邮件主键数字，或主题/发件人里的关键词，例如「银行」「账单」）、" +
                "segments（默认 false 只看第一屏；正文很长、需要看底部时传 true）。\n" +
                "只给画面，不返回原文；要原始头/源文本请用 mail_raw。看不了脚本、也不会加载远程图片。");
    }

    private static string BuildRenderCaption(MailBrief mail, MailRenderOutcome outcome)
    {
        var lines = new List<string>
        {
            $"【{mail.Subject}】渲染完成：{outcome.Images.Count} 张（发件人 {mail.From}）",
        };

        foreach (var image in outcome.Images)
        {
            lines.Add($"- 第 {image.Index + 1} 张：{image.Label}（{image.Bytes.Length} 字节）");
        }

        if (!string.IsNullOrWhiteSpace(outcome.Note))
        {
            lines.Add(outcome.Note);
        }

        lines.Add("（画面里的文字以图片为准；图片是截图，可能因字号很小而看不清，看不出来就直说。）");
        return string.Join('\n', lines);
    }

    // ───────────── 原文（通道 B） ─────────────

    private static AIFunction CreateRaw(
        ToolCallContext context,
        IPermissionGate gate,
        IMailReader reader,
        IConfigStore configs,
        ILogger logger)
    {
        return AIFunctionFactory.Create(
            async (string mail, int? maxChars = null, CancellationToken ct = default) =>
            {
                if (Deny(gate, ToolCapability.MailRaw, context) is { } denied)
                {
                    return denied;
                }

                var located = await LocateAsync(reader, mail, ct).ConfigureAwait(false);
                if (located.Problem is { } problem)
                {
                    return problem;
                }

                var picked = located.Mail!;
                // 0 / 负数 = 让服务端用配置默认值（配置项才是唯一真相，工具不另立一份默认）
                var limit = maxChars is > 0 ? maxChars.Value : 0;

                try
                {
                    var outcome = await reader.ReadRawAsync(picked.Id, limit, ct).ConfigureAwait(false);
                    if (outcome.Error is { } error)
                    {
                        logger.Warning(
                            "mail_raw 失败：mailId={MailId} 原因={Error}", picked.Id, error);
                        return error;
                    }

                    await configs
                        .AppendAuditAsync(
                            "mailRaw",
                            Actor,
                            $"id={picked.Id} 截断={outcome.Truncated} 正文={outcome.Body.Length} 字",
                            ct)
                        .ConfigureAwait(false);

                    return BuildRawText(picked, outcome);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "mail_raw 未预期失败：mailId={MailId}", picked.Id);
                    return $"读取原文失败：{ex.Message}（如实告诉对方没读到，不要拿摘要冒充满）";
                }
            },
            name: RawToolName,
            description:
                "读一封邮件的**原文**：完整邮件头（含 Received 链）+ 正文源文本（text/plain 与 text/html）。\n" +
                "参数：mail（邮件主键数字，或主题/发件人里的关键词）、maxChars（正文上限，默认取服务端配置）。\n" +
                "用于核对发件人真伪、看隐藏内容、验证码、退订链接等摘要里没有的东西；" +
                "**附件内容不在其中**。敏感操作，每次调用都会被记录。");
    }

    private static string BuildRawText(MailBrief mail, MailRawOutcome outcome)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("【").Append(mail.Subject).Append("】原文（发件人 ").Append(mail.From).AppendLine("）");

        if (!string.IsNullOrWhiteSpace(outcome.Mime))
        {
            builder.Append("Content-Type: ").AppendLine(outcome.Mime);
        }

        builder.AppendLine();
        builder.AppendLine("── 邮件头 ──");
        builder.AppendLine(string.IsNullOrWhiteSpace(outcome.Headers) ? "（没有可用的头）" : outcome.Headers);
        builder.AppendLine();
        builder.AppendLine("── 正文源文本 ──");
        builder.AppendLine(string.IsNullOrWhiteSpace(outcome.Body) ? "（正文为空）" : outcome.Body);

        if (outcome.Truncated)
        {
            builder.AppendLine();
            builder.AppendLine("⚠️ 正文已按上限截断：后面还有内容，只是没给你。需要更完整的部分就说清楚要哪一段。");
        }

        return builder.ToString().TrimEnd();
    }

    // ───────────── 公用 ─────────────

    /// <summary>定位结果：要么唯一命中，要么给一句可以直接回给模型的话。</summary>
    private sealed record LocateOutcome(MailBrief? Mail, string? Problem);

    private static async Task<LocateOutcome> LocateAsync(
        IMailReader reader, string reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new LocateOutcome(
                null, "要给一封邮件：邮件主键数字，或主题/发件人里的关键词（例如「银行」）。");
        }

        var hits = await reader.LocateAsync(reference.Trim(), ct).ConfigureAwait(false);

        if (hits.Count == 0)
        {
            return new LocateOutcome(null, $"没找到和「{reference}」对应的邮件。换个关键词试试，或先说清是哪封。");
        }

        if (hits.Count == 1)
        {
            return new LocateOutcome(hits[0], null);
        }

        // 多个候选**不替模型决定**：挑错一封等于把别人的邮件内容倒出来。
        var listed = hits
            .Take(MaxCandidates)
            .Select(hit => $"- #{hit.Id}｜{hit.SentAt:yyyy-MM-dd HH:mm}｜{hit.Subject}（{hit.From}）");

        return new LocateOutcome(
            null,
            $"「{reference}」对上不止一封邮件，请挑一个再调我一次：\n" + string.Join('\n', listed));
    }

    private static string? Deny(IPermissionGate gate, ToolCapability capability, ToolCallContext context)
    {
        var decision = gate.Check(capability, context);
        return decision.Allowed ? null : decision.Reason;
    }
}
