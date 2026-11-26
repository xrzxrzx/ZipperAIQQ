namespace ZipperAIQQ.Server.Douyin;

/// <summary>
/// 写闸门的判定结果。
/// </summary>
/// <param name="Allowed">准不准这一次。</param>
/// <param name="Reason">不准的原因（人话，会直接回给模型）。</param>
/// <param name="RetryAfter">大概多久之后可以再试（能算出来就给，算不出来为 <c>null</c>）。</param>
public sealed record WriteDecision(bool Allowed, string? Reason, TimeSpan? RetryAfter)
{
    /// <summary>放行。</summary>
    public static WriteDecision Allow() => new(true, null, null);

    /// <summary>拒绝（必须带原因：模型要据此决定"是等一会儿还是干脆别写"）。</summary>
    /// <param name="reason">人话原因。</param>
    /// <param name="retryAfter">多久后可以再试。</param>
    public static WriteDecision Deny(string reason, TimeSpan? retryAfter = null) =>
        new(false, reason, retryAfter);
}
