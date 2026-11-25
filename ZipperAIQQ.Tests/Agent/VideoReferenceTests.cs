using ZipperAIQQ.Agent.Tools;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// 视频引用解析：模型手里的三种写法都得认（占位符 / 链接 / 裸数字）。
/// </summary>
/// <remarks>
/// 这一步最容易被当成"随便 find 一串数字"，但抄错一位的后果是去查一条不存在的视频，
/// 而错误表现又会和"视频已删"一模一样 —— 所以 ≥8 位、按优先级取，逐条锁住。
/// </remarks>
public sealed class VideoReferenceTests
{
    [Theory]
    // 入站占位符原文（sidecar 发出的 `[视频 id=…]`）
    [InlineData("[视频 id=7706296015465071715]", "7706296015465071715")]
    [InlineData("视频 id=7706296015465071715", "7706296015465071715")]
    [InlineData("id: 7706296015465071715", "7706296015465071715")]
    // 作品链接
    [InlineData("https://www.douyin.com/video/7706296015465071715", "7706296015465071715")]
    [InlineData("https://www.iesdouyin.com/share/video/7706296015465071715/?region=CN", "7706296015465071715")]
    [InlineData("https://www.douyin.com/discover?modal_id=7706296015465071715", "7706296015465071715")]
    // 裸数字
    [InlineData("7706296015465071715", "7706296015465071715")]
    [InlineData("  7706296015465071715  ", "7706296015465071715")]
    // 夹在话里
    [InlineData("帮我看下这条 7706296015465071715 说了什么", "7706296015465071715")]
    public void Recognizes_three_shapes(string reference, string expected)
    {
        Assert.True(VideoReference.TryParseItemId(reference, out var itemId));
        Assert.Equal(expected, itemId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026")]                        // 年份不是 id
    [InlineData("15 秒的地方")]                  // 时长不是 id
    [InlineData("帮我看看这条视频")]             // 压根没给 id
    [InlineData("https://www.douyin.com/video/")] // 链接缺 id
    [InlineData("13800138000")]                 // 手机号不是 id（裸数字下限 15 位）
    [InlineData("订单号 20261126 查一下")]        // 8 位日期不是 id
    public void Rejects_when_no_plausible_id(string? reference)
    {
        Assert.False(VideoReference.TryParseItemId(reference, out var itemId));
        Assert.Equal(string.Empty, itemId);
    }

    [Fact]
    public void Explicit_shapes_still_accept_shorter_ids()
    {
        // 显式写法（id= / /video/）放宽到 8 位：老作品可能短，而这里不存在"句子里的数字"的歧义
        Assert.True(VideoReference.TryParseItemId("id=12345678", out var byId));
        Assert.Equal("12345678", byId);

        Assert.True(VideoReference.TryParseItemId("https://www.douyin.com/video/12345678", out var byPath));
        Assert.Equal("12345678", byPath);
    }

    [Fact]
    public void Explicit_id_wins_over_other_numbers()
    {
        // 链接里同时带 modal_id 与别的数字时，按 /video/<id> 与 id= 的优先级取
        Assert.True(VideoReference.TryParseItemId(
            "https://www.douyin.com/video/7706296015465071715?from=1234567890", out var itemId));
        Assert.Equal("7706296015465071715", itemId);
    }
}
