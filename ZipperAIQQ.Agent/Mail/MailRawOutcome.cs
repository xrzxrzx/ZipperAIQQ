namespace ZipperAIQQ.Agent.Mail;

/// <summary>
/// 原文通道的结果（头 + 正文源文本）。
/// </summary>
/// <param name="Mime">顶层 <c>Content-Type</c> 原始值；取不到时为空串。</param>
/// <param name="Headers">完整头块（每字段一行，保留 <c>Received</c> 链）。</param>
/// <param name="Body"><c>text/plain</c> 与 <c>text/html</c> 的**源文本**（不是剥过标签的）。</param>
/// <param name="Truncated">是否被字符上限截断（模型必须如实告诉对方「后面还有」）。</param>
/// <param name="Error">失败原因；成功时为 <see langword="null"/>。</param>
public sealed record MailRawOutcome(
    string Mime,
    string Headers,
    string Body,
    bool Truncated,
    string? Error)
{
    /// <summary>构造一个失败结果。</summary>
    public static MailRawOutcome Failed(string error) =>
        new(string.Empty, string.Empty, string.Empty, false, error);
}
