using Microsoft.Extensions.Hosting;
using Serilog;

namespace ZipperAIQQ.Server.Process;

/// <summary>重启服务端（问题记录 #36）。</summary>
public interface IServerRestartService
{
    /// <summary>接受重启请求：先让响应写完，再优雅停止进程。</summary>
    bool RequestRestart();
}

/// <summary>
/// 通过优雅停止实现「重启」。
/// </summary>
/// <remarks>
/// <para>
/// 进程自己**无法原地复活** —— 重新拉起要靠外部守护（<c>scripts/run-server.ps1</c> 循环启动）。
/// 这里只负责把当前进程干净地停下来，退出码 0 表示"是正常重启，不是崩溃"。
/// </para>
/// <para>
/// 延迟 500ms 再停：让 HTTP 响应先写回去，否则客户端只会看到连接被重置。
/// </para>
/// </remarks>
public sealed class ServerRestartService : IServerRestartService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger _logger;

    public ServerRestartService(IHostApplicationLifetime lifetime, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);

        _lifetime = lifetime;
        _logger = logger;
    }

    public bool RequestRestart()
    {
        _logger.Warning("收到重启请求：500ms 后优雅停止（由外部守护脚本重新拉起）");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                _lifetime.StopApplication();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "停止进程失败");
            }
        });

        return true;
    }
}
