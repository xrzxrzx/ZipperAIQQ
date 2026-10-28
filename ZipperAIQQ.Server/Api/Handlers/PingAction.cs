using System.Text.Json;
using ZipperAIQQ.Server.Api;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>连通性检查：无副作用，返回服务端时间与版本。</summary>
/// <remarks>与 <c>docs/05-契约/command-ping.json</c> 一致。</remarks>
public sealed class PingAction : IApiAction
{
    /// <summary>服务端协议 / 实现版本（客户端据此判断兼容性）。</summary>
    public const string ServerVersion = "0.5.1";

    public string Name => "ping";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        return Task.FromResult<object?>(new
        {
            serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            serverVersion = ServerVersion,
        });
    }
}
