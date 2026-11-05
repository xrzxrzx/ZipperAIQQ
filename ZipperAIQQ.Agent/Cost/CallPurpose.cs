namespace ZipperAIQQ.Agent.Cost;

/// <summary>这次模型调用有多关键（D-03 的分级表）。</summary>
/// <remarks>
/// 放在 Agent 项目：压缩 / 漂移重试在 Agent 里发起，额度闸门在 Server 里执行，
/// 只有「双方都能引用」的层才能共用同一份标记。
/// </remarks>
public enum CallPurpose
{
    /// <summary>没标注的默认值：按**关键**处理（宁可少拦，不可误拦真人）。</summary>
    Unknown = 0,
    UserReply = 1,
    Command = 2,
    MailDigest = 3,
    Compression = 4,
    DriftRetry = 5,
    SchedulePolish = 6,
    Judge = 7,
    Search = 8,
    Embedding = 9,
}

/// <summary>调用关键性作用域（<see cref="AsyncLocal{T}"/>，不必给每个调用点加参数）。</summary>
public static class CallPurposeScope
{
    private static readonly AsyncLocal<CallPurpose?> CurrentPurpose = new();

    public static CallPurpose Current => CurrentPurpose.Value ?? CallPurpose.Unknown;

    /// <summary>在作用域内标记用途；<c>using</c> 结束后恢复。</summary>
    public static IDisposable Begin(CallPurpose purpose)
    {
        var previous = CurrentPurpose.Value;
        CurrentPurpose.Value = purpose;
        return new Restore(previous);
    }

    /// <summary>是否属于**非关键**调用（到硬线可以被拦）。</summary>
    public static bool IsNonCritical(CallPurpose purpose) => purpose switch
    {
        CallPurpose.Unknown or CallPurpose.UserReply or CallPurpose.Command => false,
        _ => true,
    };

    private sealed class Restore(CallPurpose? previous) : IDisposable
    {
        public void Dispose() => CurrentPurpose.Value = previous;
    }
}
