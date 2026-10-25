using Microsoft.Extensions.AI;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Agent.Media;

/// <summary>把一条图片消息变成模型可读的 <see cref="AIContent"/>。</summary>
public static class ImageContentBuilder
{
    public static async Task<AIContent> BuildAsync(
        ImageSegment image,
        IImageFetcher? fetcher,
        IImageConverter? converter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (fetcher is null || string.IsNullOrWhiteSpace(image.Url))
        {
            return new TextContent("[图片]");
        }

        FetchedImage? fetched;
        try
        {
            fetched = await fetcher.FetchAsync(image.Url, image.FileId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TextContent($"[图片：无法读取（{ex.Message}）]");
        }

        if (fetched is null || fetched.Bytes.Length == 0)
        {
            return new TextContent("[图片：无法读取（链接可能已过期）]");
        }

        var bytes = fetched.Bytes;
        var mime = fetched.Mime;

        // GIF / WebP 取首帧转 PNG：模型对动图支持不稳，静帧更可靠
        if (ImageProbe.NeedsConversion(mime) && converter is not null)
        {
            var png = converter.TryConvertToPng(bytes, mime);
            if (png is { Length: > 0 })
            {
                bytes = png;
                mime = "image/png";
            }
        }

        return new DataContent(bytes, mime);
    }
}
