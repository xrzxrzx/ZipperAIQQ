using System.Text.Json;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.External;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>读取外部 WS 配置（问题记录 03 #5）。</summary>
public sealed class GetExternalAction : IApiAction
{
    private readonly ExternalOptionsService _external;

    public GetExternalAction(ExternalOptionsService external)
    {
        ArgumentNullException.ThrowIfNull(external);
        _external = external;
    }

    public string Name => "getExternal";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var current = _external.Current;
        return Task.FromResult<object?>(new
        {
            allowSend = current.AllowSend,
            allowedSessions = current.AllowedSessions,
            forwardUrl = current.ForwardUrl ?? string.Empty,
            hasForwardToken = current.HasForwardToken,
            reverseServers = current.ReverseServers.Select(s => new
            {
                host = s.Host,
                port = s.Port,
                hasToken = !string.IsNullOrWhiteSpace(s.Token),
            }),
        });
    }
}

/// <summary>保存外部 WS 配置；forwardToken 留空表示保持原值。</summary>
public sealed class SetExternalAction : IApiAction
{
    private readonly ExternalOptionsService _external;

    public SetExternalAction(ExternalOptionsService external)
    {
        ArgumentNullException.ThrowIfNull(external);
        _external = external;
    }

    public string Name => "setExternal";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 { allowSend, allowedSessions[], forwardUrl, forwardToken }。");
        }

        var allowSend = payload.TryGetProperty("allowSend", out var allowElement) &&
                        allowElement.ValueKind == JsonValueKind.True;

        var sessions = new List<string>();
        if (payload.TryGetProperty("allowedSessions", out var sessionsElement) &&
            sessionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sessionsElement.EnumerateArray())
            {
                var text = item.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (!text.StartsWith("qq:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ApiException(ApiResponse.BadRequest, $"白名单格式不对：{text}（应以 qq: 开头）。");
                }

                sessions.Add(text);
            }
        }
        else if (payload.TryGetProperty("allowedSessions", out var raw) &&
                 raw.ValueKind == JsonValueKind.String)
        {
            sessions.AddRange((raw.GetString() ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var forwardUrl = payload.TryGetProperty("forwardUrl", out var urlElement) &&
                         urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;

        if (forwardUrl.Length > 0 &&
            (!Uri.TryCreate(forwardUrl, UriKind.Absolute, out var url) || url.Scheme is not ("ws" or "wss")))
        {
            throw new ApiException(ApiResponse.BadRequest, "forwardUrl 必须是 ws:// 或 wss:// 地址。");
        }

        var token = payload.TryGetProperty("forwardToken", out var tokenElement) &&
                    tokenElement.ValueKind == JsonValueKind.String
            ? tokenElement.GetString()?.Trim()
            : null;

        // 反向监听块：host 只允许 0.0.0.0 / 127.0.0.1；token 留空 = 沿用同 host:port 的旧值
        var reverse = new List<ExternalReverseServer>();
        var existing = _external.Current.ReverseServers;
        if (payload.TryGetProperty("reverseServers", out var reverseElement) &&
            reverseElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reverseElement.EnumerateArray())
            {
                var host = item.TryGetProperty("host", out var hostElement) &&
                           hostElement.ValueKind == JsonValueKind.String
                    ? hostElement.GetString()?.Trim()
                    : null;

                if (host is null || !ExternalReverseServer.AllowedHosts.Contains(host, StringComparer.Ordinal))
                {
                    throw new ApiException(ApiResponse.BadRequest, "反向监听 IP 只支持 0.0.0.0 或 127.0.0.1。");
                }

                var port = item.TryGetProperty("port", out var portElement) &&
                           portElement.ValueKind == JsonValueKind.Number &&
                           portElement.TryGetInt32(out var parsedPort)
                    ? parsedPort
                    : 0;

                if (port is < 1024 or > 65535)
                {
                    throw new ApiException(ApiResponse.BadRequest, "反向监听端口必须在 1024~65535 之间。");
                }

                if (reverse.Any(s => s.Host == host && s.Port == port))
                {
                    throw new ApiException(ApiResponse.BadRequest, $"重复的监听：{host}:{port}");
                }

                var itemToken = item.TryGetProperty("token", out var itemTokenElement) &&
                                itemTokenElement.ValueKind == JsonValueKind.String
                    ? itemTokenElement.GetString()?.Trim()
                    : null;

                if (string.IsNullOrWhiteSpace(itemToken))
                {
                    itemToken = existing
                        .FirstOrDefault(s => s.Host == host && s.Port == port)?.Token;
                }

                reverse.Add(new ExternalReverseServer(host, port, itemToken));
            }
        }

        await _external.UpdateAsync(allowSend, sessions, forwardUrl, token, reverse, ct).ConfigureAwait(false);

        return new
        {
            allowSend,
            allowedSessions = sessions,
            forwardUrl,
            hasForwardToken = _external.Current.HasForwardToken,
            reverseServers = reverse.Select(s => new
            {
                host = s.Host,
                port = s.Port,
                hasToken = !string.IsNullOrWhiteSpace(s.Token),
            }),
        };
    }
}
