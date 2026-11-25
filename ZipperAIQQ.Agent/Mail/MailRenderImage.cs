namespace ZipperAIQQ.Agent.Mail;

/// <summary>
/// 渲染出来的一段画面。
/// </summary>
/// <param name="Index">第几张（从 0 开始）；模型在回答里要能说「第 2 张」。</param>
/// <param name="Bytes">JPEG 字节（直接进上下文当图片，不落盘）。</param>
/// <param name="Mime">MIME 类型（当前固定 <c>image/jpeg</c>）。</param>
/// <param name="Label">人话位置说明（如「第 1/3 段（顶部）」）。</param>
public sealed record MailRenderImage(int Index, byte[] Bytes, string Mime, string Label);
