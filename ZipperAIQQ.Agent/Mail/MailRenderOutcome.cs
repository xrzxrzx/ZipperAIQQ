namespace ZipperAIQQ.Agent.Mail;

/// <summary>
/// 渲染通道的结果：要么有图，要么有一句人话原因。
/// </summary>
/// <param name="Images">渲染出来的画面（失败时为空）。</param>
/// <param name="Note">补充说明（如「正文较长，只给了前 3 段」）；没有时为空串。</param>
/// <param name="Error">失败原因；成功时为 <see langword="null"/>。</param>
/// <remarks>
/// 刻意不做成「抛异常」：失败原因要**原样回填给模型**（它得据此换路子，
/// 而不是以为工具坏了反复重试）；真正意外的异常由调用方兜底并写日志。
/// </remarks>
public sealed record MailRenderOutcome(
    IReadOnlyList<MailRenderImage> Images,
    string Note,
    string? Error)
{
    /// <summary>构造一个失败结果。</summary>
    public static MailRenderOutcome Failed(string error) => new([], string.Empty, error);
}
