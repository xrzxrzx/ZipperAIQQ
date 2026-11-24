using System.Net;
using System.Text;
using System.Text.Json;
using ZipperAIQQ.Agent.Llm;

namespace ZipperAIQQ.Tests.Ai;

/// <summary>
/// 对话增强注入（T2.6）：**合并** tools 数组、按开关写 <c>safety_check</c> / <c>title</c>、
/// 只碰对话路径、坏 JSON 原样放行。
/// </summary>
/// <remarks>
/// 参数形状照官方指南（<c>guides/build_a_chatbot</c> / <c>guides/safety</c>）：
/// <c>tools:[{type:"deepseek_web_search"}]</c>、<c>safety_check:{level:"high"}</c>、<c>title:{enabled:true}</c>。
/// </remarks>
public sealed class DeepSeekEnhanceHandlerTests
{
    private const string BaseBody = """
        {"model":"deepseek-flash","input":"你好",
         "tools":[{"type":"function","name":"send_message","parameters":{"type":"object"}}]}
        """;

    [Fact]
    public void Disabled_options_leave_the_body_untouched()
    {
        Assert.Null(DeepSeekEnhanceHandler.Inject(BaseBody, new DeepSeekEnhanceOptions()));
    }

    [Fact]
    public void Server_tools_are_appended_and_function_tools_are_kept()
    {
        var options = new DeepSeekEnhanceOptions { WebSearch = true, ReadUrl = true };

        var json = DeepSeekEnhanceHandler.Inject(BaseBody, options);

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var tools = doc.RootElement.GetProperty("tools").EnumerateArray().ToList();

        Assert.Equal(3, tools.Count);
        Assert.Equal("function", tools[0].GetProperty("type").GetString());   // 原有工具还在
        Assert.Equal("send_message", tools[0].GetProperty("name").GetString());
        Assert.Contains(tools, t => t.GetProperty("type").GetString() == "deepseek_web_search");
        Assert.Contains(tools, t => t.GetProperty("type").GetString() == "deepseek_read_url");
    }

    [Fact]
    public void Injection_is_idempotent()
    {
        var options = new DeepSeekEnhanceOptions { WebSearch = true };

        var once = DeepSeekEnhanceHandler.Inject(BaseBody, options)!;
        var twice = DeepSeekEnhanceHandler.Inject(once, options)!;

        using var doc = JsonDocument.Parse(twice);
        var count = doc.RootElement.GetProperty("tools").EnumerateArray()
            .Count(t => t.GetProperty("type").GetString() == "deepseek_web_search");

        Assert.Equal(1, count);
    }

    [Fact]
    public void Safety_and_title_written_in_the_official_shape()
    {
        var options = new DeepSeekEnhanceOptions { SafetyCheckHigh = true, Title = true };

        var json = DeepSeekEnhanceHandler.Inject(BaseBody, options)!;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("high", root.GetProperty("safety_check").GetProperty("level").GetString());
        Assert.True(root.GetProperty("title").GetProperty("enabled").GetBoolean());
        // 没开联网工具就不该出现服务端 tools 条目
        Assert.DoesNotContain("deepseek_web_search", json);
    }

    [Fact]
    public void Tools_array_is_created_when_the_request_had_none()
    {
        var body = """{"model":"deepseek-flash","input":"hi"}""";

        var json = DeepSeekEnhanceHandler.Inject(
            body, new DeepSeekEnhanceOptions { WebSearch = true })!;

        using var doc = JsonDocument.Parse(json);
        Assert.Single(doc.RootElement.GetProperty("tools").EnumerateArray());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("")]
    public void Unparsable_bodies_pass_through_instead_of_throwing(string body)
    {
        var json = DeepSeekEnhanceHandler.Inject(body, new DeepSeekEnhanceOptions { Title = true });
        Assert.Null(json);
    }

    [Theory]
    [InlineData("https://api.deepseek.com/responses", true)]
    [InlineData("https://api.deepseek.com/v1/responses", true)]
    [InlineData("https://api.deepseek.com/chat/completions", true)]
    [InlineData("https://api.deepseek.com/responses/", true)]
    [InlineData("https://api.deepseek.com/models", false)]
    [InlineData("https://api.deepseek.com/v1/files", false)]
    public void Only_chat_paths_are_rewritten(string url, bool expected)
        => Assert.Equal(expected, DeepSeekEnhanceHandler.IsChatRequest(new Uri(url)));

    [Fact]
    public async Task Handler_rewrites_the_outbound_body_for_responses_only()
    {
        var inner = new CapturingHandler();
        var handler = new DeepSeekEnhanceHandler(new DeepSeekEnhanceOptions { WebSearch = true })
        {
            InnerHandler = inner,
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://api.deepseek.com/responses")
        {
            Content = new StringContent(BaseBody, Encoding.UTF8, "application/json"),
        };

        await invoker.SendAsync(request, CancellationToken.None);

        Assert.Contains("deepseek_web_search", inner.LastBody);
        // 功能工具没被顶掉 —— 顶掉就等于这一轮丢光全部工具能力
        Assert.Contains("send_message", inner.LastBody);

        // 非对话路径：一个字节都不改
        var inner2 = new CapturingHandler();
        var handler2 = new DeepSeekEnhanceHandler(new DeepSeekEnhanceOptions { WebSearch = true })
        {
            InnerHandler = inner2,
        };
        using var invoker2 = new HttpMessageInvoker(handler2);
        using var models = new HttpRequestMessage(
            HttpMethod.Get, "https://api.deepseek.com/models");

        await invoker2.SendAsync(models, CancellationToken.None);
        Assert.Null(inner2.LastBody);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
