using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ZipperAIQQ.Client.Dto;

namespace ZipperAIQQ.Client.Services;

/// <summary>
/// 调服务端 <c>POST /api</c> 的薄客户端（M2-D）。
/// </summary>
/// <remarks>
/// 统一把 <c>{code,message,data}</c> 拆开：<c>code != 0</c> 直接抛带原因的业务异常，
/// 页面上就能显示"服务端说哪里不对"，而不是一个空白。
/// </remarks>
public sealed class ServerApiClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly AppSettings _settings;

    public ServerApiClient(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>调一个 action，返回强类型 data。</summary>
    public async Task<T?> CallAsync<T>(string action, object? payload = null, CancellationToken ct = default)
    {
        var baseUrl = _settings.ServerUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("还没填服务器地址。");
        }

        var body = JsonSerializer.Serialize(new { action, payload }, ClientJson.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(_settings.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token.Trim());
        }

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        ApiResponse<T>? result;
        try
        {
            result = JsonSerializer.Deserialize<ApiResponse<T>>(text, ClientJson.Options);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"服务端返回的不是契约 JSON（HTTP {(int)response.StatusCode}）：{Truncate(text)}");
        }

        if (result is null)
        {
            throw new InvalidOperationException("服务端返回为空。");
        }

        if (result.Code != 0)
        {
            throw new InvalidOperationException($"服务端返回 {result.Code}：{result.Message}");
        }

        return result.Data;
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
