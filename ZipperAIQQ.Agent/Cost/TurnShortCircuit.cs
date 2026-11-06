namespace ZipperAIQQ.Agent.Cost;

/// <summary>
/// 通道熔断时要不要跳过这一轮（F-3）。
/// </summary>
/// <remarks>
/// <para>
/// 只跳**非关键**轮次：邮件汇总、定时润色这类，通道断了再问模型也发不出去。
/// 真人消息（<see cref="CallPurpose.UserReply"/>）不跳 —— 一级阈值下仍要尽力回复，
/// 真正做到「二级全短路」还需要入站排队（归 R-04 的会话队列）。
/// </para>
/// <para>
/// 没标用途（<see cref="CallPurpose.Unknown"/>）按关键处理：宁可多花一次，不可漏回真人。
/// </para>
/// </remarks>
public static class TurnShortCircuit
{
    public static bool ShouldSkip(bool channelDown, CallPurpose purpose) =>
        channelDown && CallPurposeScope.IsNonCritical(purpose);
}
