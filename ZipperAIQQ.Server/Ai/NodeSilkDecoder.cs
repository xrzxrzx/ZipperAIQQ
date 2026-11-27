using System.Diagnostics;
using Serilog;
using SysProcess = System.Diagnostics.Process;

namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// 用 **Node + silk-wasm** 把 QQ 语音解成 WAV（B00001）。
/// </summary>
/// <remarks>
/// <para>
/// 实测（2026-11-28，家里那台服务器）：<c>silk-wasm</c> 解一条 1.74 秒的语音只要 **38 ms**，
/// 纯 JS/WASM、无二进制依赖（包本身就 283 KB，已 vendored 进仓库，部署时不用 npm）。
/// </para>
/// <para>
/// 起外置进程而不是进程内解：.NET 侧没有成熟可用的 SILK 解码器，
/// 而服务器上本来就有 Node（抖音 sidecar 在用）—— 与 <c>MailRenderService</c> 同一种做法。
/// </para>
/// <para>
/// 失败一律返回 <c>null</c> 并记日志：解不出来不该让整条消息丢失。
/// </para>
/// </remarks>
public sealed class NodeSilkDecoder : ISilkDecoder
{
    private readonly AiOptions _options;
    private readonly ILogger _logger;

    public NodeSilkDecoder(AiOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    public async Task<byte[]?> ToWavAsync(byte[] silk, CancellationToken ct = default)
    {
        if (silk is null || silk.Length == 0)
        {
            return null;
        }

        var script = ResolveScriptPath();
        if (script is null)
        {
            _logger.Warning(
                "SILK 解码脚本不存在（{Script}），语音只能依赖 NapCat 侧转码", _options.SilkDecodeScript);
            return null;
        }

        var workDir = Path.Combine(Path.GetFullPath(_options.AsrTempDir), Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(workDir);

            var inPath = Path.Combine(workDir, "in.silk");
            var outPath = Path.Combine(workDir, "out.wav");
            await File.WriteAllBytesAsync(inPath, silk, ct).ConfigureAwait(false);

            var node = string.IsNullOrWhiteSpace(_options.NodePath) ? "node" : _options.NodePath;

            var psi = new ProcessStartInfo(node)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };

            psi.Environment["TEMP"] = workDir;
            psi.Environment["TMP"] = workDir;

            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(inPath);
            psi.ArgumentList.Add(outPath);

            using var process = new SysProcess { StartInfo = psi };

            if (!process.Start())
            {
                _logger.Warning("SILK 解码进程启动失败（{Node}）", node);
                return null;
            }

            // stderr 必须同时读走，否则管道写满会把子进程卡死
            var stderr = process.StandardError.ReadToEndAsync(ct);
            var stdout = process.StandardOutput.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                TryKill(process);
                _logger.Warning("SILK 解码超时（15s），已放弃");
                return null;
            }

            var outText = (await stdout.ConfigureAwait(false)).Trim();
            var errText = (await stderr.ConfigureAwait(false)).Trim();

            if (process.ExitCode != 0 || !File.Exists(outPath))
            {
                _logger.Warning(
                    "SILK 解码失败（exit {Code}）：{Out} {Err}",
                    process.ExitCode, Shorten(outText), Shorten(errText));
                return null;
            }

            var wav = await File.ReadAllBytesAsync(outPath, ct).ConfigureAwait(false);
            if (wav.Length <= 44)
            {
                _logger.Warning("SILK 解码产出为空 WAV（{Bytes} 字节）", wav.Length);
                return null;
            }

            return wav;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 最常见：机器上没有 node（Win32Exception: 系统找不到指定的文件）
            _logger.Warning(ex, "SILK 解码异常（node 不可用？）");
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }
            }
            catch (IOException)
            {
                // 子进程可能还握着句柄：临时目录残留不影响功能
            }
        }
    }

    /// <summary>
    /// 解析脚本路径：相对路径按**当前工作目录**算（服务端从部署根目录启动），
    /// 找不到再试「发布目录」旁边（部署时 tools/ 与 exe 同级）。
    /// </summary>
    private string? ResolveScriptPath()
    {
        var configured = _options.SilkDecodeScript;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var direct = Path.GetFullPath(configured);
        if (File.Exists(direct))
        {
            return direct;
        }

        var besideExe = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, configured));
        return File.Exists(besideExe) ? besideExe : null;
    }

    private static void TryKill(SysProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 已经退出了
        }
    }

    private static string Shorten(string text) =>
        text.Length <= 300 ? text.ReplaceLineEndings(" ") : text[..300].ReplaceLineEndings(" ");
}
