using System.Text.Json;
using ZipperAIQQ.Server.External;

namespace ZipperAIQQ.Tests.Server;

/// <summary>外部 WS 帧协议（问题记录 03 #5）。</summary>
public sealed class ExternalProtocolTests
{
    [Fact]
    public void HelloFrame_HasVersionAndSelfId()
    {
        using var doc = JsonDocument.Parse(ExternalProtocol.Frame("hello", new { version = "1", selfId = "10001" }));

        Assert.Equal("hello", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("1", doc.RootElement.GetProperty("data").GetProperty("version").GetString());
    }

    [Fact]
    public void EventFrame_HasKindSessionPayload()
    {
        using var doc = JsonDocument.Parse(ExternalProtocol.Event("message", "qq:p1", new { text = "hi" }));
        var data = doc.RootElement.GetProperty("data");

        Assert.Equal("message", data.GetProperty("kind").GetString());
        Assert.Equal("qq:p1", data.GetProperty("session").GetString());
        Assert.Equal("hi", data.GetProperty("payload").GetProperty("text").GetString());
    }

    [Fact]
    public void AckFrame_ReportsRequestIdAndOk()
    {
        using var doc = JsonDocument.Parse(ExternalProtocol.Ack("r1", false, "nope"));
        var data = doc.RootElement.GetProperty("data");

        Assert.Equal("r1", data.GetProperty("requestId").GetString());
        Assert.False(data.GetProperty("ok").GetBoolean());
        Assert.Equal("nope", data.GetProperty("error").GetString());
    }

    [Fact]
    public void TryParse_RejectsInvalidJson()
    {
        Assert.False(ExternalProtocol.TryParse("{not json", out _, out var error));
        Assert.NotNull(error);
    }
}
