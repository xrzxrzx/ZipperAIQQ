using ZipperAIQQ.Channel.Failures;

namespace ZipperAIQQ.Agent.Reply;

/// <summary>
/// 漂移重试的**前置判据**：这一轮到底该不该再问模型一次。
/// </summary>
/// <remarks>
/// <para>
/// 旧判据只有「<c>sent == 0</c> 且产出了文本」，它把两种完全不同的情况混成一件事：
/// </para>
/// <list type="number">
///   <item>模型没调工具 —— 再提醒一次有意义；</item>
///   <item><b>通道坏了</b> —— 再问多少次都发不出去，纯烧 token（11-05 事故）。</item>
/// </list>
/// <para>
/// 所以这里把「本轮最后一次发送失败的原因」也纳入判断：通道不可用 → 不重试。
/// 发送失败（超时 / 非 ok）仍允许重试一次 —— 那是瞬时问题，重试有意义。
/// </para>
/// </remarks>
public static class DriftRetryPolicy
{
    public static bool ShouldRetry(int sent, string? finalText, FailureKind? lastSendFailure)
    {
        if (sent > 0 || string.IsNullOrWhiteSpace(finalText))
        {
            return false;
        }

        // 只有「重试真有帮助」的情况才重试一次：
        //   · 没有失败记录 / 未知 → 仍按老行为试一次；
        //   · 发送失败（超时、非 ok）→ 瞬时问题，值得重试；
        // 其余（通道不可用 / 目标非法 / 内容被拒 / 限流 / 模型硬失败）重试同一条都只会再失败一次。
        return lastSendFailure switch
        {
            null => true,
            FailureKind.ChannelSendFailed => true,
            FailureKind.Unknown => true,
            FailureKind.ModelTransient => true,
            _ => false,
        };
    }
}
