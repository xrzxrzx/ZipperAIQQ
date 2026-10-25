using System.Text.Json;
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
            reverseServers = current.ReverseServers.Select(s => new
            {
                host = s.Host,
                port = s.Port,
                hasToken = !string.IsNullOrWhiteSpace(s.Token),
            }),
            forwardServers = current.ForwardServers.Select(s => new
            {
                url = s.Url,
                hasToken = !string.IsNullOrWhiteSpace(s.Token),
            }),
        });
    }
}

/// <summary>保存外部 WS 配置；每块 token 留空表示沿用同 host:port / url 的旧值。</summary>
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
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 { allowSend, allowedSessions[], reverseServers[], forwardServers[] }。");
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

        var existingReverse = _external.Current.ReverseServers;
        var reverse = new List<ExternalReverseServer>();
        if (payload.TryGetProperty("reverseServers", out var reverseElement) &&
            reverseElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reverseElement.EnumerateArray())
            {
                var host = item.TryGetProperty("host", out var hostElement) &&
                           hostElement.ValueKind == JsonValueKind.String
                    ? hostElement.GetString()?.Trim()
                    : null;

                if (string.IsNullOrWhiteSpace(host))
                {
                    throw new ApiException(ApiResponse.BadRequest, "反向监听的主机不能为空。");
                }

                var port = item.TryGetProperty("port", out var portElement) &&
                           portElement.ValueKind == JsonValueKind.Number &&
                           portElement.TryGetInt32(out var parsedPort)
                    ? parsedPort
                    : 0;

                if (port is < 1 or > 65535)
                {
                    throw new ApiException(ApiResponse.BadRequest, "反向监听端口必须在 1~65535 之间。");
                }

                if (reverse.Any(s => s.Host == host && s.Port == port))
                {
                    throw new ApiException(ApiResponse.BadRequest, $"重复的监听：{host}:{port}");
                }

                var token = item.TryGetProperty("token", out var tokenElement) &&
                            tokenElement.ValueKind == JsonValueKind.String
                    ? tokenElement.GetString()?.Trim()
                    : null;

                if (string.IsNullOrWhiteSpace(token))
                {
                    token = existingReverse
                        .FirstOrDefault(s => s.Host == host && s.Port == port)?.Token;
                }

                reverse.Add(new ExternalReverseServer(host, port, token));
            }
        }

        var existingForward = _external.Current.ForwardServers;
        var forward = new List<ExternalForwardServer>();
        if (payload.TryGetProperty("forwardServers", out var forwardElement) &&
            forwardElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in forwardElement.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var urlElement) &&
                          urlElement.ValueKind == JsonValueKind.String
                    ? urlElement.GetString()?.Trim()
                    : null;

                if (string.IsNullOrWhiteSpace(url) ||
                    !Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
                    parsedUrl.Scheme is not ("ws" or "wss"))
                {
                    throw new ApiException(ApiResponse.BadRequest, $"正向 WS 地址不合法：{url}");
                }

                if (forward.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ApiException(ApiResponse.BadRequest, $"重复的正向地址：{url}");
                }

                var token = item.TryGetProperty("token", out var tokenElement) &&
                            tokenElement.ValueKind == JsonValueKind.String
                    ? tokenElement.GetString()?.Trim()
                    : null;

                if (string.IsNullOrWhiteSpace(token))
                {
                    token = existingForward
                        .FirstOrDefault(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase))?.Token;
                }

                forward.Add(new ExternalForwardServer(url, token));
            }
        }

        await _external.UpdateAsync(allowSend, sessions, reverse, forward, ct).ConfigureAwait(false);

        return new
        {
            allowSend,
            allowedSessions = sessions,
            reverseServers = reverse.Select(s => new { host = s.Host, port = s.Port, hasToken = !string.IsNullOrWhiteSpace(s.Token) }),
            forwardServers = forward.Select(s => new { url = s.Url, hasToken = !string.IsNullOrWhiteSpace(s.Token) }),
        };
    }
}
