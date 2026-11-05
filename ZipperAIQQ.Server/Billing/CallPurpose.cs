namespace ZipperAIQQ.Server.Billing;

/// <summary>这次模型调用有多关键（D-03 的分级表）。</summary>
/// <remarks>
/// 额度到硬线时只拦**非关键**调用；真人消息与命令永远放行 ——
/// 否则会出现「额度见底，连问一句都回不了」。
/// </remarks>
public enum CallPurpose
{
    /// <summary>没标注的默认值：按**关键**处理（宁可少拦，不可误拦真人）。</summary>
    Unknown = 0,

    /// <summary>真人消息的回复。</summary>
    UserReply = 1,

    /// <summary>命令（<c>/status</c> 等）。</summary>
    Command = 2,

    /// <summary>邮件汇总里的人格 agent。</summary>
    MailDigest = 3,

    /// <summary>后台上下文压缩。</summary>
    Compression = 4,

    /// <summary>漂移纠正重试。</summary>
    DriftRetry = 5,

    /// <summary>定时任务润色。</summary>
    SchedulePolish = 6,

    /// <summary>JEV 判断。</summary>
    Judge = 7,

    /// <summary>网页搜索 / 抓取。</summary>
    Search = 8,

    /// <summary>向量 / 嵌入。</summary>
    Embedding = 9,
}

/// <summary>调用关键性作用域（<see cref="AsyncLocal{T}"/>，不必给每个调用点加参数）。</summary>
public static class CallPurposeScope
{
    private static readonly AsyncLocal<CallPurpose?> CurrentPurpose = new();

    public static CallPurpose Current => CurrentPurpose.Value ?? CallPurpose.Unknown;

    /// <summary>在作用域内把当前调用标记为某用途；<c>using</c> 结束后恢复。</summary>
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
