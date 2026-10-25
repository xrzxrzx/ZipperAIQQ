namespace ZipperAIQQ.Agent.Media;

/// <summary>下载到的图片字节与真实 MIME。</summary>
public sealed record FetchedImage(byte[] Bytes, string Mime);

/// <summary>取图片字节（Server 用 HTTP 实现；测试可替换）。</summary>
public interface IImageFetcher
{
    /// <summary>失败（链接过期 / 超时 / 超大）返回 null，不抛给模型链路。</summary>
    Task<FetchedImage?> FetchAsync(string url, string? fileId, CancellationToken ct = default);
}

/// <summary>
/// 动图转静图（GIF / WebP 首帧 → PNG）。
/// </summary>
/// <remarks>
/// 接口放 Agent、实现放 Server（SkiaSharp）：Agent 不直接依赖图像库。
/// 转换失败返回 null，调用方回退原字节。
/// </remarks>
public interface IImageConverter
{
    byte[]? TryConvertToPng(byte[] data, string mime);
}
