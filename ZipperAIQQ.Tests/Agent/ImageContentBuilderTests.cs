using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Media;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>图片投喂（问题记录 03 #2）：成功、转换、失败占位。</summary>
public sealed class ImageContentBuilderTests
{
    private sealed class FakeFetcher : IImageFetcher
    {
        private readonly FetchedImage? _image;
        private readonly Exception? _error;

        public FakeFetcher(FetchedImage? image = null, Exception? error = null)
        {
            _image = image;
            _error = error;
        }

        public Task<FetchedImage?> FetchAsync(string url, string? fileId, CancellationToken ct = default)
        {
            if (_error is not null)
            {
                throw _error;
            }

            return Task.FromResult(_image);
        }
    }

    private sealed class FakeConverter : IImageConverter
    {
        private readonly byte[]? _png;

        public FakeConverter(byte[]? png)
        {
            _png = png;
        }

        public byte[]? TryConvertToPng(byte[] data, string mime) => _png;
    }

    private static ImageSegment Image() => new("https://example.com/a.jpg", "file");

    [Fact]
    public async Task Gif_IsConvertedToPng()
    {
        var gif = new FetchedImage("GIF89a"u8.ToArray(), "image/gif");
        var content = await ImageContentBuilder.BuildAsync(
            Image(), new FakeFetcher(gif), new FakeConverter([0x89, 0x50, 0x4E, 0x47]));

        var data = Assert.IsType<DataContent>(content);
        Assert.Equal("image/png", data.MediaType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, data.Data.ToArray());
    }

    [Fact]
    public async Task Jpeg_IsPassedThroughWithRealMime()
    {
        var jpeg = new FetchedImage([0xFF, 0xD8, 0xFF, 0xE0], "image/jpeg");
        var content = await ImageContentBuilder.BuildAsync(
            Image(), new FakeFetcher(jpeg), new FakeConverter(null));

        var data = Assert.IsType<DataContent>(content);
        Assert.Equal("image/jpeg", data.MediaType);
    }

    [Fact]
    public async Task NullFetcher_UsesPlaceholder()
    {
        var content = await ImageContentBuilder.BuildAsync(Image(), null, null);
        Assert.Equal("[图片]", Assert.IsType<TextContent>(content).Text);
    }

    [Fact]
    public async Task FetchFailure_UsesPlaceholder()
    {
        var content = await ImageContentBuilder.BuildAsync(
            Image(), new FakeFetcher(null), new FakeConverter(null));

        Assert.Contains("无法读取", Assert.IsType<TextContent>(content).Text);
    }

    [Fact]
    public async Task FetchException_UsesPlaceholder()
    {
        var content = await ImageContentBuilder.BuildAsync(
            Image(), new FakeFetcher(error: new HttpRequestException("400")), null);

        Assert.Contains("无法读取", Assert.IsType<TextContent>(content).Text);
    }
}
