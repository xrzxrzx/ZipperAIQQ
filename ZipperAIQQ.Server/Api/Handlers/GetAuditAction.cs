using System.Text.Json;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>最近的配置审计（FR-8.9）：只有"谁在何时对什么做了什么"，没有内容。</summary>
public sealed class GetAuditAction : IApiAction
{
    private readonly IConfigStore _store;

    public GetAuditAction(IConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public string Name => "getAudit";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var limit = Math.Clamp(PayloadReader.GetInt(payload, "limit", 20), 1, 200);
        var entries = await _store.ListAuditAsync(limit, ct).ConfigureAwait(false);

        return entries
            .Select(e => new
            {
                action = e.Action,
                actor = e.Actor,
                target = e.Target,
                at = e.At.ToUnixTimeMilliseconds(),
            })
            .ToList();
    }
}
