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
                name = s.Name,
                host = s.Host,
                port = s.Port,
                token = s.Token ?? string.Empty,
                enabled = s.Enabled,
            }),
            forwardServers = current.ForwardServers.Select(s => new
            {
                name = s.Name,
                url = s.Url,
                token = s.Token ?? string.Empty,
                enabled = s.Enabled,
            }),
        });
    }
}

/// <summary>保存外部 WS 配置（反向 / 正向都是列表，各自可启停）。</summary>
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

        var reverse = new List<ExternalReverseServer>();
        if (payload.TryGetProperty("reverseServers", out var reverseElement) &&
            reverseElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in reverseElement.EnumerateArray())
            {
                var host = ReadString(item, "host") ?? string.Empty;
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

                reverse.Add(new ExternalReverseServer
                {
                    Name = ReadString(item, "name") ?? "反向监听",
                    Host = host.Trim(),
                    Port = port,
                    Token = NullIfEmpty(ReadString(item, "token")),
                    Enabled = ReadBool(item, "enabled", true),
                });
            }
        }

        var forward = new List<ExternalForwardServer>();
        if (payload.TryGetProperty("forwardServers", out var forwardElement) &&
            forwardElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in forwardElement.EnumerateArray())
            {
                var url = ReadString(item, "url") ?? string.Empty;
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

                forward.Add(new ExternalForwardServer
                {
                    Name = ReadString(item, "name") ?? "正向连接",
                    Url = url.Trim(),
                    Token = NullIfEmpty(ReadString(item, "token")),
                    Enabled = ReadBool(item, "enabled", true),
                });
            }
        }

        await _external.UpdateAsync(allowSend, sessions, reverse, forward, ct).ConfigureAwait(false);

        return new
        {
            allowSend,
            allowedSessions = sessions,
            reverseServers = reverse.Select(s => new { name = s.Name, host = s.Host, port = s.Port, token = s.Token ?? string.Empty, enabled = s.Enabled }),
            forwardServers = forward.Select(s => new { name = s.Name, url = s.Url, token = s.Token ?? string.Empty, enabled = s.Enabled }),
        };
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim()
            : null;

    private static bool ReadBool(JsonElement item, string name, bool fallback) =>
        item.TryGetProperty(name, out var element) &&
        element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : fallback;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
