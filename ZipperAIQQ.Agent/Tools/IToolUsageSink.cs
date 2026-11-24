namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 工具用量记账（问题记录 06 T2.7）：按**计费单位**记一次调用，不混进 token 口径。
/// </summary>
/// <remarks>
/// <para>
/// 为什么接口放 Agent、实现放 Server：Agent 知道「这次 OCR 花了几页 / 这次 TTS 多少字符」，
/// 但落库是 Server 的事（Agent 不碰数据库实现）。
/// </para>
/// <para>
/// 记账失败**不能让工具调用失败**：用量是观测，工具结果是交付。
/// 实现方必须自己吞异常并记日志，不给调用方抛。
/// </para>
/// </remarks>
public interface IToolUsageSink
{
    /// <summary>
    /// 记一次用量。
    /// </summary>
    /// <param name="kind">工具类别，如 <c>ocr</c> / <c>tts</c>。</param>
    /// <param name="unit">计费单位：<c>page</c> / <c>char</c> / <c>second</c>。</param>
    /// <param name="amount">数量（页数 / 字符数 / 秒数）。</param>
    /// <param name="sessionId">会话 ID（用于按会话筛选；可空）。</param>
    Task RecordAsync(
        string kind, string unit, double amount, string? sessionId, CancellationToken ct = default);
}
