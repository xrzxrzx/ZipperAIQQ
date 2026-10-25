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

        await _external.UpdateAsync(allowSend, sessions, forwardUrl, token, ct).ConfigureAwait(false);

        return new
        {
            allowSend,
            allowedSessions = sessions,
            forwardUrl,
            hasForwardToken = _external.Current.HasForwardToken,
        };
    }
}
