namespace ZipperAIQQ.Server.Media;

/// <summary>
/// 视频详情（够抽样与转写用的最小集合）。
/// </summary>
/// <param name="ItemId">作品 id（纯数字）。</param>
/// <param name="Title">作品文案；平台允许没有文案，此时为空串。</param>
/// <param name="DurationMs">时长（毫秒）；取不到时为 0（调用方据此按「不知道时长」处理）。</param>
/// <param name="PlayUrls">候选播放地址（已按「先 mp4」排过序）。</param>
public sealed record DouyinVideoInfo(
    string ItemId,
    string Title,
    long DurationMs,
    IReadOnlyList<string> PlayUrls);
