using Serilog;
using SkiaSharp;
using ZipperAIQQ.Agent.Media;

namespace ZipperAIQQ.Server.Media;

/// <summary>
/// 用 SkiaSharp 把 GIF / WebP 取首帧转成 PNG。
/// </summary>
/// <remarks>
/// 动图在模型侧支持不稳；转静帧后既有画面又不会因格式被拒。
/// 转换失败返回 null，调用方回退原字节（至少还能试一次）。
/// </remarks>
public sealed class SkiaImageConverter : IImageConverter
{
    private readonly ILogger _logger;

    public SkiaImageConverter(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public byte[]? TryConvertToPng(byte[] data, string mime)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(data);
            if (bitmap is null)
            {
                _logger.Warning("动图解码失败（{Mime}，{Bytes} 字节），回退原格式", mime, data.Length);
                return null;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            return encoded?.ToArray();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "动图转 PNG 异常（{Mime}），回退原格式", mime);
            return null;
        }
    }
}
