using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>会话 ID 解析（问题记录 03 #3）。</summary>
public sealed class SessionRefParserTests
{
    [Theory]
    [InlineData("qq:p3406515483", ChatType.Private, "3406515483")]
    [InlineData("qq:g123456", ChatType.Group, "123456")]
    [InlineData("QQ:P1", ChatType.Private, "1")]
    public void ParsesValidRefs(string raw, ChatType type, string id)
    {
        Assert.True(SessionRefParser.TryParse(raw, "qq", out var chat, out var error));
        Assert.Null(error);
        Assert.Equal("qq", chat.Platform);
        Assert.Equal(type, chat.Type);
        Assert.Equal(id, chat.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3406515483")]
    [InlineData("qq:")]
    [InlineData("qq:x123")]
    [InlineData("qq:p")]
    [InlineData("qq:p12a")]
    [InlineData("tg:p1")]
    public void RejectsInvalidRefs(string raw)
    {
        Assert.False(SessionRefParser.TryParse(raw, "qq", out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
