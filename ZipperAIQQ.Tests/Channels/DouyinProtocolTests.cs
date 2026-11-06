using System.Text.Json.Nodes;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Tests.Channels;

/// <summary>
/// 抖音出站帧：图片段的两种来源（URL / 服务器内联 base64）。
/// </summary>
/// <remarks>
/// 服务器自己生成的图（<c>/status</c>、<c>/help</c>）没有 URL，字节放在
/// <see cref="ImageSegment.FileId"/> 的 <c>base64://</c> 前缀里（见 <c>CommandRouter.ReplyAsync</c>）。
/// 旧实现只看 URL → 直接降级成文字「[图片]」，用户只能看到一个方括号（2026-11-05 实测）。
/// </remarks>
public sealed class DouyinProtocolTests
{
    private static JsonObject Outbound(params MessageSegment[] segments)
    {
        var json = DouyinProtocol.BuildOutbound("douyin:p123", segments, replyTo: null, requestId: "r1");
        // 帧形状：{ type: "outbound", data: { session, segments, requestId } }
        return JsonNode.Parse(json)!.AsObject()["data"]!.AsObject();
    }

    [Fact]
    public void Inline_base64_image_becomes_data_segment()
    {
        var frame = Outbound(new ImageSegment(null, "base64://QUJD"));

        var segment = frame["segments"]!.AsArray()[0]!.AsObject();
        Assert.Equal("image", segment["type"]!.GetValue<string>());
        Assert.Equal("QUJD", segment["data"]!.GetValue<string>());
        Assert.Null(segment["url"]);
    }

    [Fact]
    public void Url_image_keeps_the_url_form()
    {
        var frame = Outbound(new ImageSegment("https://example.com/a.png", null));

        var segment = frame["segments"]!.AsArray()[0]!.AsObject();
        Assert.Equal("image", segment["type"]!.GetValue<string>());
        Assert.Equal("https://example.com/a.png", segment["url"]!.GetValue<string>());
        Assert.Null(segment["data"]);
    }

    [Fact]
    public void Image_without_url_or_base64_degrades_to_text_placeholder()
    {
        var frame = Outbound(new ImageSegment(null, "file-1234"));

        var segment = frame["segments"]!.AsArray()[0]!.AsObject();
        Assert.Equal("text", segment["type"]!.GetValue<string>());
        Assert.Equal("[图片]", segment["text"]!.GetValue<string>());
    }

    [Fact]
    public void Text_then_image_keeps_order()
    {
        var frame = Outbound(new TextSegment("看图"), new ImageSegment(null, "base64://QUJD"));

        var segments = frame["segments"]!.AsArray();
        Assert.Equal(2, segments.Count);
        Assert.Equal("text", segments[0]!.AsObject()["type"]!.GetValue<string>());
        Assert.Equal("image", segments[1]!.AsObject()["type"]!.GetValue<string>());
    }
}
