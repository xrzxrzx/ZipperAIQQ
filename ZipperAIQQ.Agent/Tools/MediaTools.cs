using System.Text;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 全家桶媒体工具两件：<c>ocr_image</c>（识别图里的字）、<c>tts_speak</c>（把话读成音频）。
/// </summary>
/// <remarks>
/// <para>
/// **两道闸**（与文件工具同口径）：① 能力不可用 / 无权限的工具**连 schema 都不给模型**；
/// ② 每次执行前再过一次门禁（防角色判定被绕过）。
/// </para>
/// <para>
/// <b>工作区路径要多一道 <see cref="ToolCapability.ReadFile"/></b>：<c>file_read</c> 默认只给白名单，
/// 而 <c>MediaRead</c> 默认给普通成员 —— 不加这道，普通成员就能用 OCR 把「读不到的文件」读成文字。
/// 消息里的图（<c>msg:N</c>）是他自己发的，只走 <c>MediaRead</c> 就够。
/// </para>
/// <para>
/// 为什么要 <c>ocr_image</c>：模型看得到图，但**不确定**图里那串小字是什么（分辨率、压缩、
/// 竖排、表格都会让它读错）；交给专门的识别接口拿**逐页文本**，比让它硬猜可靠。
/// </para>
/// <para>
/// 为什么要 <c>tts_speak</c>：把长文本变成可听的产物（主人路上听），
/// 但**本期只落文件**，不自动往会话发语音（Q-07 拍板；通道语音能力另案）。
/// </para>
/// <para>
/// 错误一律**如实回填**（<see cref="DeepSeekMediaException.ToModelMessage"/>）：
/// 吞掉失败会让模型以为「图里没字」，然后编一个出来。兜底异常同时**写日志** ——
/// 只在模型那边说一句"没做成"、日志里一条没有，等于运维侧瞎一只眼。
/// </para>
/// </remarks>
public static class MediaTools
{
    public const string OcrToolName = "ocr_image";
    public const string TtsToolName = "tts_speak";

    /// <summary>OCR 结果回填给模型的上限（超出截断并标注，别把上下文吃干）。</summary>
    private const int MaxOcrChars = 8000;

    /// <summary>消息里「第 N 张图」的引用前缀。</summary>
    private const string MessageImagePrefix = "msg:";

    public static IReadOnlyList<AITool> Create(
        ToolCallContext context,
        IPermissionGate gate,
        WorkspacePathResolver workspace,
        int maxUploadBytes,
        IDeepSeekMediaClient client,
        IImageFetcher? imageFetcher,
        IReadOnlyList<MessageSegment>? segments,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);

        var tools = new List<AITool>(2);
        var uploadLimit = Math.Max(1, maxUploadBytes);

        if (client.OcrAvailable && gate.IsAllowed(ToolCapability.MediaRead, context.Role))
        {
            tools.Add(CreateOcr(context, gate, workspace, uploadLimit, client, imageFetcher, segments, logger));
        }

        if (client.TtsAvailable && gate.IsAllowed(ToolCapability.MediaSpeak, context.Role))
        {
            tools.Add(CreateTts(context, gate, client, logger));
        }

        return tools;
    }

    // ───────────── OCR ─────────────

    private static AIFunction CreateOcr(
        ToolCallContext context,
        IPermissionGate gate,
        WorkspacePathResolver workspace,
        int maxUploadBytes,
        IDeepSeekMediaClient client,
        IImageFetcher? imageFetcher,
        IReadOnlyList<MessageSegment>? segments,
        ILogger logger)
    {
        return AIFunctionFactory.Create(
            async (string source, string? purpose = null, CancellationToken ct = default) =>
            {
                if (Deny(gate, ToolCapability.MediaRead, context) is { } denied)
                {
                    return denied;
                }

                if (string.IsNullOrWhiteSpace(source))
                {
                    return "请给出要识别的图片来源：本会话工作区里的相对路径，或 msg:2（本轮消息里的第 2 张图）。";
                }

                var load = await ResolveImageAsync(
                        source.Trim(), workspace, context, gate, maxUploadBytes, imageFetcher, segments, ct)
                    .ConfigureAwait(false);

                switch (load)
                {
                    case ImageError error:
                        return error.Message;

                    case LoadedImage image:
                        try
                        {
                            var result = await client
                                .OcrAsync(image.Bytes, image.FileName, ParsePurpose(purpose), ct)
                                .ConfigureAwait(false);

                            return FormatOcr(image.FileName, result);
                        }
                        catch (DeepSeekMediaException ex)
                        {
                            logger.Warning(
                                "ocr_image 失败：source={Source} kind={Kind} status={Status} {Message}",
                                source, ex.Kind, ex.StatusCode, ex.Message);
                            return ex.ToModelMessage();
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "ocr_image 未预期失败：source={Source}", source);
                            return $"识别失败：{ex.Message}（如实告诉对方这次没做成，不要编造图里的内容）";
                        }

                    default:
                        return "识别失败：图片来源解析异常。";
                }
            },
            name: OcrToolName,
            description:
                "识别图片里的文字（OCR），返回逐页文本。适合看清小字、截图、表格、扫描件。\n" +
                "参数：source（本会话工作区里的相对路径，或 msg:N 表示本轮消息里的第 N 张图）、" +
                "purpose（text 只要文字 / layout 保留版面，默认 text）。\n" +
                "只返回识别结果，不做翻译或总结；图里没有可读文字时如实说没有。");
    }

    /// <summary>把 <c>msg:N</c> / 工作区路径解析成字节 + 文件名；失败返回可读原因。</summary>
    private static async Task<ImageSource> ResolveImageAsync(
        string source,
        WorkspacePathResolver workspace,
        ToolCallContext context,
        IPermissionGate gate,
        int maxUploadBytes,
        IImageFetcher? imageFetcher,
        IReadOnlyList<MessageSegment>? segments,
        CancellationToken ct)
    {
        if (source.StartsWith(MessageImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var raw = source[MessageImagePrefix.Length..].Trim();
            if (!int.TryParse(raw, out var wanted) || wanted <= 0)
            {
                return new ImageError("msg:N 里的 N 要从 1 开始的整数，例如 msg:1。");
            }

            if (imageFetcher is null)
            {
                return new ImageError("当前没有可用的图片下载通道，请改用工作区里的文件路径。");
            }

            // 与 ChatAgent 渲染上下文的谓词**保持一致**（只有绝对 URL 才会被真当成图投喂），
            // 否则模型数的"第 N 张"和这里数的会错位。
            var images = (segments ?? [])
                .OfType<ImageSegment>()
                .Where(i => !string.IsNullOrWhiteSpace(i.Url) &&
                            Uri.TryCreate(i.Url, UriKind.Absolute, out _))
                .ToList();

            if (images.Count == 0)
            {
                return new ImageError("本轮消息里没有图片。");
            }

            if (wanted > images.Count)
            {
                return new ImageError($"本轮消息只有 {images.Count} 张图，没有第 {wanted} 张。");
            }

            var picked = images[wanted - 1];
            var fetched = await imageFetcher.FetchAsync(picked.Url!, picked.FileId, ct).ConfigureAwait(false);
            if (fetched is null)
            {
                return new ImageError($"第 {wanted} 张图现在取不到（链接可能过期）。");
            }

            if (fetched.Bytes.Length > maxUploadBytes)
            {
                return new ImageError(
                    $"第 {wanted} 张图 {fetched.Bytes.Length} 字节，超过上传上限 {maxUploadBytes}。");
            }

            return new LoadedImage(fetched.Bytes, $"msg{wanted}{ExtensionForMime(fetched.Mime)}");
        }

        // 读**工作区里的文件**属于 file_read 的范畴：必须再过一次 ReadFile 门禁，
        // 否则 MediaRead=Normal 会变成绕过 ReadFile=Whitelist 的旁路。
        if (Deny(gate, ToolCapability.ReadFile, context) is { } readDenied)
        {
            return new ImageError(readDenied);
        }

        if (!workspace.TryResolve(context.Chat, source, out var absolute, out var error))
        {
            return new ImageError(error!);
        }

        if (!File.Exists(absolute))
        {
            return new ImageError($"文件不存在：{source}");
        }

        var info = new FileInfo(absolute);
        if (info.Length > maxUploadBytes)
        {
            return new ImageError($"文件太大（{info.Length} 字节，上限 {maxUploadBytes}）：{source}");
        }

        var bytes = await File.ReadAllBytesAsync(absolute, ct).ConfigureAwait(false);
        return new LoadedImage(bytes, Path.GetFileName(absolute));
    }

    private static string FormatOcr(string fileName, OcrResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【{fileName}】识别完成：共 {result.PageCount} 页"
                      + (result.Pages.Count == 0 ? "（平台未返回逐页文本）" : $"，逐页给出 {result.Pages.Count} 页"));
        sb.AppendLine();

        foreach (var page in result.Pages)
        {
            var text = page.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            sb.AppendLine($"[第 {page.Index} 页]");
            sb.AppendLine(text);
            sb.AppendLine();
        }

        var body = sb.ToString().TrimEnd();
        if (body.Length > MaxOcrChars)
        {
            body = body[..MaxOcrChars] + "\n…（识别结果过长，已截断）";
        }

        // 计费口径回填：页是主单位（1.5 元/千页），token 是平台自报的**用量**，别跟对话 token 混成一账
        var usage = new List<string> { $"{result.PageCount} 页" };
        if (result.InputTokens is { } inTok && result.OutputTokens is { } outTok)
        {
            usage.Add($"平台用量 input/output {inTok}/{outTok}");
        }

        return body + "\n（计费口径：" + string.Join(" / ", usage) + "）";
    }

    // ───────────── TTS ─────────────

    private static AIFunction CreateTts(
        ToolCallContext context, IPermissionGate gate, IDeepSeekMediaClient client, ILogger logger)
    {
        return AIFunctionFactory.Create(
            async (string text, string? voiceId = null, CancellationToken ct = default) =>
            {
                if (Deny(gate, ToolCapability.MediaSpeak, context) is { } denied)
                {
                    return denied;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return "请给出要朗读的文本。";
                }

                try
                {
                    var result = await client.TtsAsync(text, voiceId, ct).ConfigureAwait(false);

                    // 如实说明产物去向：本期**只落文件**，不会自己发语音消息。
                    // 也**只给文件名**：服务器绝对路径对模型没用，还会把部署结构漏出去。
                    return $"语音已生成：{Path.GetFileName(result.FilePath)}"
                           + $"（{result.Bytes} 字节，约 {result.Characters} 字符，{result.Format}）\n"
                           + "文件落在服务器的语音目录里；本期只落文件、不自动发语音消息，"
                           + "需要发出去请让人在客户端处理。";
                }
                catch (DeepSeekMediaException ex)
                {
                    logger.Warning(
                        "tts_speak 失败：chars={Chars} kind={Kind} status={Status} {Message}",
                        text.Length, ex.Kind, ex.StatusCode, ex.Message);
                    return ex.ToModelMessage();
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "tts_speak 未预期失败：chars={Chars}", text.Length);
                    return $"语音生成失败：{ex.Message}（如实告诉对方没做成）";
                }
            },
            name: TtsToolName,
            description:
                "把文本朗读成语音文件（TTS），产物落到服务器本地。\n" +
                "参数：text（要读的文字，≤10000 字符）、voiceId（可选：stella 女声 / tide 男声，默认 stella）。\n" +
                "只生成文件，不会自动发语音消息；不要声称已经发出去了。");
    }

    // ───────────── 公用 ─────────────

    private static string? Deny(IPermissionGate gate, ToolCapability capability, ToolCallContext context)
    {
        var decision = gate.Check(capability, context);
        return decision.Allowed ? null : decision.Reason;
    }

    private static OcrPurpose ParsePurpose(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            return OcrPurpose.TextRecognition;
        }

        var raw = purpose.Trim();
        return raw.ToLowerInvariant() switch
        {
            "layout" or "rich" or "rich_layout_recognition" or "richlayoutrecognition"
                => OcrPurpose.RichLayoutRecognition,
            _ => OcrPurpose.TextRecognition,
        };
    }

    private static string ExtensionForMime(string mime) => mime.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => ".jpg",
    };

    /// <summary>图片来源解析结果（成功 / 失败两态，避免用 <c>!</c> 消音掩盖契约）。</summary>
    private abstract record ImageSource;

    private sealed record LoadedImage(byte[] Bytes, string FileName) : ImageSource;

    private sealed record ImageError(string Message) : ImageSource;
}
