using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Mail;

// ⚠️ 本项目有 ZipperAIQQ.Server.Process 命名空间，裸写 Process 会被解析成它（CS0118）。
using SysProcess = System.Diagnostics.Process;

namespace ZipperAIQQ.Server.Mail;

/// <summary>
/// 邮件 HTML 渲染器：调用外置的一次性 CLI（<c>tools/mail-render/render.mjs</c>）截图。
/// </summary>
/// <remarks>
/// <para>
/// 为什么外置（方案 G4）：截图要 Chromium，而服务端必须跨平台、且不该为了一张图把浏览器
/// 焊进 .NET 进程。仓库里已有 Playwright（<c>douyin-web-sidecar</c>），共用同一份浏览器安装。
/// </para>
/// <para>
/// **降级口径**：脚本或 Node 缺失 → <see cref="IsAvailable"/> 为 false、给出人话原因，
/// 服务端照常启动、<c>mail_render</c> 工具不出现。绝不"工具在但每次都失败"。
/// </para>
/// <para>
/// 安全默认全在脚本那侧（禁 JS、拦外链、像素上限）；这里只负责传参、超时与清理，
/// 不重复实现一套策略 —— 两处各写一份必然漂移。
/// </para>
/// </remarks>
public sealed class MailRenderService : IMailRenderer
{
    /// <summary>临时目录根（每次调用一个子目录，跑完删掉）。</summary>
    private readonly string _tempRoot;

    private readonly MailOptions _options;
    private readonly ILogger _logger;
    private readonly string _scriptPath;

    public MailRenderService(MailOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
        _scriptPath = Path.GetFullPath(options.RenderScriptPath);
        _tempRoot = Path.GetFullPath(options.RenderTempDir);

        (IsAvailable, UnavailableReason) = Probe();
    }

    /// <summary>渲染是否可用（脚本 + Node 都能用）。</summary>
    public bool IsAvailable { get; }

    /// <summary>不可用的人话原因；可用时为空串。</summary>
    public string UnavailableReason { get; }

    /// <summary>
    /// 把 HTML 渲染成 JPEG 图片。
    /// </summary>
    /// <param name="html">邮件 HTML 源文本。</param>
    /// <param name="segments">true = 按高度切段（最多 <see cref="MailOptions.RenderMaxImages"/> 张）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidOperationException">渲染器不可用，或外置脚本回报失败。</exception>
    public async Task<RenderedMail> RenderAsync(string html, bool segments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);

        if (!IsAvailable)
        {
            throw new InvalidOperationException(UnavailableReason);
        }

        var workDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var htmlPath = Path.Combine(workDir, "mail.html");
            await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false), ct).ConfigureAwait(false);

            var outputDir = Path.Combine(workDir, "out");
            var json = await RunScriptAsync(htmlPath, outputDir, workDir, segments, ct).ConfigureAwait(false);

            return await ParseResultAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            // 截图是中间产物：转成内存字节后就没用了。留着只会让服务器上堆满垃圾。
            TryDeleteDirectory(workDir);
        }
    }

    /// <summary>启动前的可用性体检（开关 → 脚本在不在 → node 跑不跑得起来）。</summary>
    private (bool Available, string Reason) Probe()
    {
        // 开关关着就别去 spawn 一个 node 进程：这台机器上没装 Node 时，
        // 「没启用」和「启用了但坏了」在日志里必须能区分开。
        if (!_options.RenderEnabled)
        {
            return (false, "邮件画面通道没开（mail.renderEnabled=false）。");
        }

        if (!File.Exists(_scriptPath))
        {
            return (false, $"渲染脚本不存在：{_scriptPath}（mail.renderScriptPath）");
        }

        try
        {
            using var process = StartProcess(["--version"], redirect: true);
            if (!process.WaitForExit(ProbeTimeoutMs))
            {
                TryKill(process);
                return (false, $"探测 `{_options.RenderNodePath} --version` 超时，渲染器不可用");
            }

            if (process.ExitCode != 0)
            {
                return (false, $"`{_options.RenderNodePath} --version` 退出码 {process.ExitCode}，渲染器不可用");
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"找不到可用的 Node（{_options.RenderNodePath}）：{ex.Message}");
        }
    }

    /// <summary>探测 node 的超时（毫秒）：只是问一句版本号，慢成这样就是坏了。</summary>
    private const int ProbeTimeoutMs = 10_000;

    /// <summary>跑外置脚本，返回它打到 stdout 的那行 JSON。</summary>
    private async Task<string> RunScriptAsync(
        string htmlPath, string outputDir, string childTempDir, bool segments, CancellationToken ct)
    {
        var maxImages = segments
            ? Math.Clamp(_options.RenderMaxImages, 1, 12)
            : 1;

        var args = new List<string>
        {
            _scriptPath,
            "--html", htmlPath,
            "--out", outputDir,
            "--max-images", maxImages.ToString(),
            "--timeout", Math.Clamp(_options.RenderTimeoutSeconds, 5, 120).ToString(),
        };

        // 超过一张就是「按段看」：单段的页面高度上限也跟着收紧，免得第一张就把整封吞了
        if (segments)
        {
            args.Add("--segment-height");
            args.Add("1400");
        }

        if (_options.RenderAllowRemote)
        {
            args.Add("--allow-remote");
        }

        if (!string.IsNullOrWhiteSpace(_options.RenderPlaywrightDir))
        {
            args.Add("--playwright");
            args.Add(Path.GetFullPath(_options.RenderPlaywrightDir));
        }

        using var process = StartProcess(args, redirect: true, childTempDir);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RenderTimeoutSeconds, 5, 120) + 15));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new InvalidOperationException(
                $"渲染超时（{_options.RenderTimeoutSeconds} 秒），已终止外置进程。");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stdout))
        {
            _logger.Warning("邮件渲染脚本没有输出：exit={ExitCode} stderr={StdErr}",
                process.ExitCode, Truncate(stderr, 500));
            throw new InvalidOperationException(
                $"渲染脚本没有输出（退出码 {process.ExitCode}）：{Truncate(stderr, 300)}");
        }

        return stdout.Trim();
    }

    /// <summary>解析脚本的 JSON 输出并读回图片字节。</summary>
    private async Task<RenderedMail> ParseResultAsync(string json, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"渲染脚本输出不是合法 JSON：{Truncate(json, 200)}", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var reason = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : "未知原因";
                throw new InvalidOperationException($"渲染失败：{reason}");
            }

            var engine = root.TryGetProperty("engine", out var engineNode) && engineNode.ValueKind == JsonValueKind.String
                ? engineNode.GetString() ?? "chromium"
                : "chromium";

            var truncated = root.TryGetProperty("truncated", out var truncatedNode)
                            && truncatedNode.ValueKind == JsonValueKind.True;

            var pageHeight = root.TryGetProperty("pageHeight", out var heightNode)
                             && heightNode.TryGetInt32(out var height)
                ? height
                : 0;

            var images = new List<MailRenderImage>();
            if (root.TryGetProperty("images", out var imagesNode) && imagesNode.ValueKind == JsonValueKind.Array)
            {
                var total = imagesNode.GetArrayLength();
                foreach (var item in imagesNode.EnumerateArray())
                {
                    var path = item.TryGetProperty("path", out var pathNode) ? pathNode.GetString() : null;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    var index = item.TryGetProperty("index", out var indexNode) && indexNode.TryGetInt32(out var parsedIndex)
                        ? parsedIndex
                        : images.Count;

                    var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                    images.Add(new MailRenderImage(
                        index,
                        bytes,
                        "image/jpeg",
                        DescribeSegment(index, total, item)));
                }
            }

            if (images.Count == 0)
            {
                throw new InvalidOperationException("渲染完成但没有产出图片。");
            }

            return new RenderedMail(images, engine, truncated, pageHeight);
        }
    }

    /// <summary>给每张图一句位置说明（模型要能说"第 2 张"）。</summary>
    private static string DescribeSegment(int index, int total, JsonElement item)
    {
        var position = index == 0 ? "顶部" : (index == total - 1 ? "末段" : "中间");
        var height = item.TryGetProperty("height", out var heightNode) && heightNode.TryGetInt32(out var h)
            ? h
            : 0;

        return total == 1
            ? "整封邮件"
            : $"第 {index + 1}/{total} 段（{position}，{height}px 高）";
    }

    /// <summary>起一个外置进程（统一口径：不建窗口、不读 shell）。</summary>
    /// <param name="arguments">参数列表。</param>
    /// <param name="redirect">是否接管 stdout/stderr。</param>
    /// <param name="childTempDir">
    /// 子进程的临时目录。**必须给**：Chromium 会在 <c>os.tmpdir()</c> 下 mkdtemp 建自己的 profile，
    /// 部署机（或受限沙箱）把系统 TEMP 收紧时，浏览器会以 <c>EPERM: mkdtemp</c> 起不来 ——
    /// 那不是"没装浏览器"，而是"没权限在 TEMP 下建目录"，报错信息却看不出来。
    /// </param>
    private SysProcess StartProcess(
        IReadOnlyList<string> arguments, bool redirect, string? childTempDir = null)
    {
        var info = new ProcessStartInfo(_options.RenderNodePath)
        {
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrWhiteSpace(childTempDir))
        {
            // Windows 看 TEMP/TMP，POSIX 看 TMPDIR —— 三个都给，跨平台不用分支
            info.Environment["TEMP"] = childTempDir;
            info.Environment["TMP"] = childTempDir;
            info.Environment["TMPDIR"] = childTempDir;
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return SysProcess.Start(info)
               ?? throw new InvalidOperationException($"无法启动外置进程：{_options.RenderNodePath}");
    }

    private void TryKill(SysProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "终止邮件渲染进程时出错（忽略）");
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // 删不掉不是失败：临时目录系统会清。但不能静默 —— 反复删不掉说明权限有问题。
            _logger.Debug(ex, "清理邮件渲染临时目录失败：{Path}", path);
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
