using ZipperAIQQ.Agent.Media;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>图片魔数识别（问题记录 03 #2）。</summary>
public sealed class ImageProbeTests
{
    [Fact]
    public void DetectsJpeg()
    {
        Assert.Equal("image/jpeg", ImageProbe.DetectMime([0xFF, 0xD8, 0xFF, 0xE0, 0x00]));
    }

    [Fact]
    public void DetectsPng()
    {
        Assert.Equal("image/png", ImageProbe.DetectMime([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
    }

    [Fact]
    public void DetectsGif()
    {
        Assert.Equal("image/gif", ImageProbe.DetectMime("GIF89a"u8.ToArray()));
        Assert.True(ImageProbe.NeedsConversion("image/gif"));
    }

    [Fact]
    public void DetectsWebp()
    {
        var bytes = new byte[16];
        "RIFF"u8.CopyTo(bytes);
        bytes[8] = (byte)'W';
        bytes[9] = (byte)'E';
        bytes[10] = (byte)'B';
        bytes[11] = (byte)'P';

        Assert.Equal("image/webp", ImageProbe.DetectMime(bytes));
        Assert.True(ImageProbe.NeedsConversion("image/webp"));
    }

    [Fact]
    public void UnknownBytesReturnNull()
    {
        Assert.Null(ImageProbe.DetectMime([1, 2, 3, 4]));
        Assert.False(ImageProbe.NeedsConversion("image/png"));
    }
}
