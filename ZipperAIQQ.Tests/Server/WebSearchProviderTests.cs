using System.Net;
using System.Text;
using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Search;
using ZipperAIQQ.Server.Search;

namespace ZipperAIQQ.Tests.Server;

/// <summary>网页搜索（#1）：官方契约、失败语义、fetch 回落。</summary>
public sealed class WebSearchProviderTests
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static DeepSeekWebSearchProvider CreateProvider(FakeHandler handler, bool enabled = true) =>
        new(
            new HttpClient(handler),
            _ => Task.FromResult<string?>("test-key"),
            enabled,
            "https://api.deepseek.test",
            new LoggerConfiguration().CreateLogger());

    [Fact]
    public async Task Search_UsesOfficialPathAndParameters()
    {
        var handler = new FakeHandler(_ => Json(
            HttpStatusCode.OK,
            """{"results":[{"title":"标题","url":"https://example.com/a","snippet":"摘要"}]}"""));

        var provider = CreateProvider(handler);
        var results = await provider.SearchAsync("猫羽雫", 3);

        Assert.Single(results);
        Assert.Equal("标题", results[0].Title);
        Assert.Equal("https://example.com/a", results[0].Url);
        Assert.Equal("摘要", results[0].Snippet);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://api.deepseek.test/v1/tools/search", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", handler.LastRequest.Headers.Authorization!.ToString());

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("猫羽雫", body.RootElement.GetProperty("query").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("count").GetInt32());
        Assert.False(body.RootElement.GetProperty("need_content").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("top_k", out _));
    }

    [Fact]
    public async Task Search_NonSuccess_ThrowsWithStatusCode()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.NotFound, """{"error":"not found"}"""));
        var provider = CreateProvider(handler);

        var ex = await Assert.ThrowsAsync<WebSearchException>(() => provider.SearchAsync("x", 1));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Search_EmptyResults_ReturnsEmpty()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"results":[]}"""));
        var provider = CreateProvider(handler);

        var results = await provider.SearchAsync("x", 1);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_MalformedResponse_Throws()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"data":{}}"""));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<WebSearchException>(() => provider.SearchAsync("x", 1));
    }

    [Fact]
    public async Task Fetch_UsesOfficialEndpoint()
    {
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/v1/tools/fetch")
            ? Json(HttpStatusCode.OK, """{"content":"# 正文"}""")
            : Json(HttpStatusCode.NotFound, "{}"));

        var provider = CreateProvider(handler);
        var text = await provider.FetchAsync("https://example.com/page");

        Assert.Equal("# 正文", text);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://api.deepseek.test/v1/tools/fetch", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task Fetch_OfficialFailure_FallsBackToSelfFetch()
    {
        var handler = new FakeHandler(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.InternalServerError, """{"error":"boom"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><head><style>p{}</style></head><body><h1>Hello</h1><script>x()</script></body></html>",
                    Encoding.UTF8,
                    "text/html"),
            };
        });

        var provider = CreateProvider(handler);
        var text = await provider.FetchAsync("https://example.com/page");

        Assert.NotNull(text);
        Assert.Contains("Hello", text);
        Assert.DoesNotContain("<h1>", text);
        Assert.DoesNotContain("x()", text);
    }

    [Fact]
    public async Task Disabled_IsNotAvailable_AndThrows()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"results":[]}"""));
        var provider = CreateProvider(handler, enabled: false);

        Assert.False(provider.IsAvailable);
        await Assert.ThrowsAsync<WebSearchException>(() => provider.SearchAsync("x", 1));
    }
}
