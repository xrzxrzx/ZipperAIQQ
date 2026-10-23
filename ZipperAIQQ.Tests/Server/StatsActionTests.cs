using System.Text.Json;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 数据页统计口径：会话筛选、缓存命中率、类别中文名。
/// </summary>
/// <remarks>
/// 直接调 action 并序列化结果来断言 —— 返回值是匿名对象，这样不用为测试改产品代码。
/// </remarks>
public sealed class StatsActionTests
{
    private sealed class FakeMetrics : IMetricsStore
    {
        public readonly List<MetricEntry> Entries = [];

        public Task RecordAsync(MetricEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MetricSummary>> GetSummaryAsync(
            int windowHours = 24,
            string? sessionId = null,
            CancellationToken ct = default)
        {
            IReadOnlyList<MetricSummary> rows = sessionId switch
            {
                "qq:p1" => [new MetricSummary("llm.call", 1, 100, 100, 100, 1000, 200, 500)],
                _ => [new MetricSummary("llm.call", 2, 150, 100, 200, 2000, 400, 1000)],
            };
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<MetricSessionInfo>> ListSessionAsync(
            int windowHours = 24,
            CancellationToken ct = default)
        {
            IReadOnlyList<MetricSessionInfo> rows =
            [
                new MetricSessionInfo("qq:p1", 1, DateTimeOffset.UtcNow),
            ];
            return Task.FromResult(rows);
        }
    }

    private static async Task<JsonElement> RunAsync(string payload)
    {
        var action = new GetStatsAction(new FakeMetrics());
        var element = JsonDocument.Parse(payload).RootElement;
        var data = await action.HandleAsync(element, CancellationToken.None);
        return JsonSerializer.SerializeToElement(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task Session_id_is_passed_through_and_echoed()
    {
        var json = await RunAsync("""{ "sessionId": "qq:p1" }""");

        Assert.Equal("qq:p1", json.GetProperty("sessionId").GetString());
        Assert.Equal(1000, json.GetProperty("allTime").GetProperty("llm").GetProperty("inputTokens").GetInt64());
    }

    [Fact]
    public async Task Missing_session_means_all_sessions()
    {
        var json = await RunAsync("{}");

        Assert.Equal(JsonValueKind.Null, json.GetProperty("sessionId").ValueKind);
        Assert.Equal(2000, json.GetProperty("allTime").GetProperty("llm").GetProperty("inputTokens").GetInt64());
    }

    [Fact]
    public async Task Cache_hit_rate_is_cached_over_input()
    {
        var json = await RunAsync("{}");
        var llm = json.GetProperty("allTime").GetProperty("llm");

        Assert.Equal(1000, llm.GetProperty("cachedInputTokens").GetInt64());
        Assert.Equal(0.5, llm.GetProperty("cacheHitRate").GetDouble(), 4);
    }

    [Fact]
    public async Task Kind_has_chinese_label_and_keeps_english()
    {
        var json = await RunAsync("{}");
        var kind = json.GetProperty("allTime").GetProperty("kinds")[0];

        Assert.Equal("llm.call", kind.GetProperty("kind").GetString());
        Assert.Equal("LLM 调用", kind.GetProperty("kindLabel").GetString());
    }

    [Fact]
    public void Unknown_kind_falls_back_to_itself()
        => Assert.Equal("brand.new", MetricKindCatalog.Label("brand.new"));
}
