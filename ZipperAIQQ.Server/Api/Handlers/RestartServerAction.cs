using System.Text.Json;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Process;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>重启服务端（#36）：只停不启，重新拉起由外部守护负责。</summary>
public sealed class RestartServerAction : IApiAction
{
    private readonly IServerRestartService _restart;

    public RestartServerAction(IServerRestartService restart)
    {
        ArgumentNullException.ThrowIfNull(restart);
        _restart = restart;
    }

    public string Name => "restartServer";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var accepted = _restart.RequestRestart();

        return Task.FromResult<object?>(new
        {
            accepted,
            note = "服务端将在 0.5 秒后停止；若没有守护脚本，需要手动重新启动。",
        });
    }
}
