using System.Net.Http.Headers;
using System.Text.Json;
using ZipperAIQQ.Storage.Billing;

namespace ZipperAIQQ.Server.Billing;

/// <summary>
/// DeepSeek 余额查询（<c>GET /user/balance</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 该接口**只返回当前余额快照**，没有历史、没有用量 —— 所以窗口花费只能靠相邻采样差推算
/// （见 <see cref="SpendCalculator"/>）。别指望它给出「今天花了多少」。
/// </para>
/// <para>
/// 币种优先 CNY；同时存在多币种时取第一条 CNY，没有就取第一条。
/// </para>
/// </remarks>
public static class DeepSeekBalanceClient
{
    public const string Provider = "deepseek";
    public const string BalancePath = "/user/balance";

    /// <summary>按基础地址拼出余额接口（末尾斜杠容错）。</summary>
    public static string BuildUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return baseUrl.TrimEnd('/') + BalancePath;
    }

    public static async Task<BalanceSample> FetchAsync(
        HttpClient http,
        string baseUrl,
        string apiKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(baseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(json, DateTimeOffset.Now)
            ?? throw new InvalidOperationException("余额接口没返回可用的 balance_infos");
    }

    /// <summary>解析响应；结构不对返回 null（**不猜**）。</summary>
    public static BalanceSample? Parse(string json, DateTimeOffset sampledAt)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var available = root.TryGetProperty("is_available", out var isAvailable) &&
                            isAvailable.ValueKind == JsonValueKind.True;

            if (!root.TryGetProperty("balance_infos", out var infos) ||
                infos.ValueKind != JsonValueKind.Array ||
                infos.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement? picked = null;
            foreach (var info in infos.EnumerateArray())
            {
                picked ??= info;
                if (info.TryGetProperty("currency", out var currency) &&
                    string.Equals(currency.GetString(), "CNY", StringComparison.OrdinalIgnoreCase))
                {
                    picked = info;
                    break;
                }
            }

            if (picked is not { } chosen)
            {
                return null;
            }

            return new BalanceSample(
                Provider,
                chosen.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "CNY" : "CNY",
                Money(chosen, "total_balance"),
                Money(chosen, "granted_balance"),
                Money(chosen, "topped_up_balance"),
                available,
                sampledAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>金额是**字符串**（"110.00"），也可能是数字；空/坏值按 0 处理。</summary>
    private static decimal Money(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0m;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(
                value.GetString(),
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ? parsed : 0m,
            JsonValueKind.Number => value.TryGetDecimal(out var number) ? number : 0m,
            _ => 0m,
        };
    }
}
