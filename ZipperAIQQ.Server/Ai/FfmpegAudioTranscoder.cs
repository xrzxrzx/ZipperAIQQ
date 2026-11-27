using System.Diagnostics;
using Serilog;
using SysProcess = System.Diagnostics.Process;

namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// 用 **ffmpeg 命令**把语音转成 ASR 要的 16 kHz 单声道 PCM WAV。
/// </summary>
/// <remarks>
/// <para>
/// 实测（2026-11-28，家里那台 **Windows Server 2016**）：
/// 机器上原有的 <c>@ffmpeg-installer</c> 那份 exe **跑不起来**
/// （<c>not a valid application for this OS platform</c> —— 它加了壳，Server 2016 拒跑）；
/// 而 npm 的 <c>ffmpeg-static</c> 6.1.1（82 MB，未加壳）**能跑**，
/// AMR → 16k mono WAV 实测通过。所以默认路径指向部署目录里的那一份。
/// </para>
/// <para>
/// 它解不了 SILK（<c>Invalid frame size</c>）—— SILK 由 <c>ISilkDecoder</c> 兜底。
/// </para>
/// <para>
/// 失败一律返回 <c>null</c> 并记日志：语音转不出来不该让整条消息丢失。
/// </para>
/// </remarks>
public sealed class FfmpegAudioTranscoder : IAudioTranscoder
{
    private const int TargetSampleRate = 16_000;

    private readonly AiOptions _options;
    private readonly ILogger _logger;

    public FfmpegAudioTranscoder(AiOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    public async Task<byte[]?> ToWavAsync(byte[] source, string? format, CancellationToken ct = default)
    {
        if (source is null || source.Length == 0)
        {
            return null;
        }

        var ffmpeg = ResolveFfmpegPath();
        if (ffmpeg is null)
        {
            _logger.Warning(
                "找不到 ffmpeg（{Path}），非 WAV 语音无法转码", _options.FfmpegPath);
            return null;
        }

        var workDir = Path.Combine(Path.GetFullPath(_options.AsrTempDir), Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(workDir);

            var inName = "in" + (string.IsNullOrWhiteSpace(format) ? ".bin" : "." + format.Trim().TrimStart('.'));
            var inPath = Path.Combine(workDir, inName);
            var outPath = Path.Combine(workDir, "out.wav");

            await File.WriteAllBytesAsync(inPath, source, ct).ConfigureAwait(false);

            var psi = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };

            psi.Environment["TEMP"] = workDir;
            psi.Environment["TMP"] = workDir;

            foreach (var arg in new[]
                     {
                         "-hide_banner", "-loglevel", "error",
                         "-i", inPath,
                         "-ac", "1",
                         "-ar", TargetSampleRate.ToString(),
                         "-f", "wav",
                         "-y", outPath,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new SysProcess { StartInfo = psi };

            if (!process.Start())
            {
                _logger.Warning("ffmpeg 启动失败（{Path}）", ffmpeg);
                return null;
            }

            // stderr 必须同时读走，否则管道写满会把子进程卡死
            var stderr = process.StandardError.ReadToEndAsync(ct);
            _ = process.StandardOutput.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                TryKill(process);
                _logger.Warning("ffmpeg 转码超时（15s），已放弃");
                return null;
            }

            var errorText = (await stderr.ConfigureAwait(false)).Trim();

            if (process.ExitCode != 0 || !File.Exists(outPath))
            {
                _logger.Warning(
                    "ffmpeg 转码失败（exit {Code}，format={Format}）：{Error}",
                    process.ExitCode, format, Shorten(errorText));
                return null;
            }

            var wav = await File.ReadAllBytesAsync(outPath, ct).ConfigureAwait(false);
            return wav.Length > 44 ? wav : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ffmpeg 不可用或转码异常（format={Format}）", format);
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
    /// 解析 ffmpeg：配置值先按**当前工作目录**算（服务端从部署根目录启动），
    /// 再试「发布目录」旁边；配置成裸命令名（如 <c>ffmpeg</c>）时直接交给系统 PATH。
    /// </summary>
    private string? ResolveFfmpegPath()
    {
        var configured = _options.FfmpegPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        // 裸命令名：交给系统解析（含 PATH）
        if (!configured.Contains(Path.DirectorySeparatorChar) && !configured.Contains('/'))
        {
            return configured;
        }

        var direct = Path.GetFullPath(configured);
        if (File.Exists(direct))
        {
            return direct;
        }

        var besideExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
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
