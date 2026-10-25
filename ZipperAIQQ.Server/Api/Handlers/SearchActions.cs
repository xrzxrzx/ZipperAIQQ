using System.Text.Json;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Search;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>读取网页搜索配置。</summary>
public sealed class GetSearchAction : IApiAction
{
    private readonly SearchOptionsService _search;

    public GetSearchAction(SearchOptionsService search)
    {
        ArgumentNullException.ThrowIfNull(search);
        _search = search;
    }

    public string Name => "getSearch";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var current = _search.Current;
        return Task.FromResult<object?>(new
        {
            enabled = current.Enabled,
            baseUrl = current.BaseUrl,
            hasKey = current.HasKey,
        });
    }
}

/// <summary>保存网页搜索配置（key 复用 primary 端点，不在这里存）。</summary>
public sealed class SetSearchAction : IApiAction
{
    private readonly SearchOptionsService _search;

    public SetSearchAction(SearchOptionsService search)
    {
        ArgumentNullException.ThrowIfNull(search);
        _search = search;
    }

    public string Name => "setSearch";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 { enabled, baseUrl }。");
        }

        var enabled = payload.TryGetProperty("enabled", out var enabledElement) &&
                      enabledElement.ValueKind == JsonValueKind.True;

        var baseUrl = payload.TryGetProperty("baseUrl", out var urlElement) &&
                      urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;

        if (baseUrl.Length > 0 &&
            (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new ApiException(ApiResponse.BadRequest, "baseUrl 必须是 http:// 或 https:// 地址。");
        }

        var apiKey = payload.TryGetProperty("apiKey", out var keyElement) &&
                     keyElement.ValueKind == JsonValueKind.String
            ? keyElement.GetString()?.Trim()
            : null;

        await _search.UpdateAsync(enabled, baseUrl, apiKey, ct).ConfigureAwait(false);

        return new
        {
            enabled,
            baseUrl = _search.Current.BaseUrl,
            hasKey = _search.Current.HasKey,
        };
    }
}
