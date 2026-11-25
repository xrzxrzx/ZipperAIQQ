namespace ZipperAIQQ.Agent.Media;

/// <summary>
/// 抽帧结果：要么有图，要么有一句人话原因。
/// </summary>
/// <param name="Frames">抽出来的帧（失败时为空）。</param>
/// <param name="Note">补充说明（视频时长 / 是否被截断）；没有时为空串。</param>
/// <param name="Error">失败原因；成功时为 <see langword="null"/>。</param>
/// <remarks>
/// 刻意做成结果对象而不是抛异常：失败原因要**原样回填给模型**（它得据此换路子，
/// 例如换一个视频、或改用转写），而不是让它以为工具坏了反复重试。
/// </remarks>
public sealed record VideoFramesOutcome(IReadOnlyList<VideoFrameImage> Frames, string Note, string? Error)
{
    /// <summary>构造一个失败结果。</summary>
    public static VideoFramesOutcome Failed(string error) => new([], string.Empty, error);
}
