using System.Text.Json;
using ZipperAIQQ.Server.Config;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>读取运行配置（**key 只回掩码**，FR-8.6）。</summary>
public sealed class GetConfigAction : IApiAction
{
    private readonly RuntimeConfigService _config;

    public GetConfigAction(RuntimeConfigService config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public string Name => "getConfig";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct) =>
        _config.GetViewAsync(ct);
}
