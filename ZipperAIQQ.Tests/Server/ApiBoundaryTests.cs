using System.Text.Json;
using Serilog;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Dto;

namespace ZipperAIQQ.Tests.Server;

/// <summary>HTTP 边界：缺 payload / 非对象 payload 的处理，以及定长 token 比较。</summary>
public sealed class ApiBoundaryTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task Undefined_payload_is_dispatched_as_empty_object()
    {
        var response = await NewDispatcher().DispatchAsync(
            new ApiRequest("kind", default), CancellationToken.None);

        Assert.Equal(ApiResponse.Ok, response.Code);
        Assert.Equal("Object", response.Data);
    }

    [Fact]
    public async Task Null_payload_is_dispatched_as_empty_object()
    {
        var nullPayload = JsonSerializer.Deserialize<JsonElement>("null");

        var response = await NewDispatcher().DispatchAsync(
            new ApiRequest("kind", nullPayload), CancellationToken.None);

        Assert.Equal(ApiResponse.Ok, response.Code);
        Assert.Equal("Object", response.Data);
    }

    [Fact]
    public async Task Array_payload_is_a_bad_request_not_a_server_error()
    {
        var arrayPayload = JsonSerializer.Deserialize<JsonElement>("[]");

        var response = await NewDispatcher().DispatchAsync(
            new ApiRequest("kind", arrayPayload), CancellationToken.None);

        Assert.Equal(ApiResponse.BadRequest, response.Code);
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "abcd", false)]
    [InlineData("", "abc", false)]
    [InlineData(null, "abc", false)]
    public void Matches_compares_length_and_content(string? provided, string? expected, bool same)
        => Assert.Equal(same, HttpAccessPolicy.Matches(provided, expected));

    private static ActionDispatcher NewDispatcher() =>
        new([new PayloadKindAction()], Logger);

    private sealed class PayloadKindAction : IApiAction
    {
        public string Name => "kind";

        public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct) =>
            Task.FromResult<object?>(payload.ValueKind.ToString());
    }
}
