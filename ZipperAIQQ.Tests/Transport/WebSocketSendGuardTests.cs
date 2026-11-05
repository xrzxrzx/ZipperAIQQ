using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Serilog;
using ZipperAIQQ.Transport;

namespace ZipperAIQQ.Tests.Transport;

/// <summary>
/// 发送前的状态守卫：**状态不对要抛，不能静默 return**（问题记录 04 #7 第 1 步）。
/// </summary>
/// <remarks>
/// 静默 return 会让上层误以为发成功，等到 echo 超时才失败 —— 那时已经分不清是
/// 「没发出去」还是「发了没回」，于是被当成模型问题又调一次模型。
/// </remarks>
public sealed class WebSocketSendGuardTests
{
    private sealed class StubTransport : WebSocketDuplexBase
    {
        public StubTransport(ILogger logger) : base(logger)
        {
        }

        protected override ValueTask OnMessageAsync(JsonNode json) => ValueTask.CompletedTask;
    }

    private static StubTransport Create() => new(new LoggerConfiguration().CreateLogger());

    [Fact]
    public async Task Send_before_attach_throws_instead_of_silently_succeeding()
    {
        await using var transport = Create();

        var ex = await Assert.ThrowsAsync<WebSocketException>(
            () => transport.SendAsync(new JsonObject { ["action"] = "x" }));

        Assert.Contains("尚未建立", ex.Message);
    }

    [Fact]
    public async Task Send_after_dispose_throws()
    {
        var transport = Create();
        await transport.DisposeAsync();

        var ex = await Assert.ThrowsAsync<WebSocketException>(
            () => transport.SendAsync(new JsonObject { ["action"] = "x" }));

        Assert.Contains("已被释放", ex.Message);
    }
}
