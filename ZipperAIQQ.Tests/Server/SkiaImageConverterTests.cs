using Serilog;
using ZipperAIQQ.Server.Media;

namespace ZipperAIQQ.Tests.Server;

/// <summary>GIF 首帧转 PNG（问题记录 03 #2）。</summary>
public sealed class SkiaImageConverterTests
{
    // 1x1 透明 GIF
    private const string OnePixelGifBase64 =
        "R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    [Fact]
    public void Gif_ConvertsToPng()
    {
        var converter = new SkiaImageConverter(new LoggerConfiguration().CreateLogger());
        var png = converter.TryConvertToPng(Convert.FromBase64String(OnePixelGifBase64), "image/gif");

        Assert.NotNull(png);
        Assert.True(png!.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    [Fact]
    public void InvalidData_ReturnsNull()
    {
        var converter = new SkiaImageConverter(new LoggerConfiguration().CreateLogger());
        Assert.Null(converter.TryConvertToPng([1, 2, 3], "image/gif"));
    }
}
