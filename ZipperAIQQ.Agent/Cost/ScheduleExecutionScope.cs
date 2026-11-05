namespace ZipperAIQQ.Agent.Cost;

/// <summary>
/// 「正在执行定时任务」的作用域（G-4：防自我繁殖）。
/// </summary>
/// <remarks>
/// 定时任务到点会投给 Agent；如果这期间它还能创建新的定时任务，
/// 就可能出现「执行 → 再创建 → 再执行」的无限链。这里给创建工具一道闸。
/// </remarks>
public static class ScheduleExecutionScope
{
    private static readonly AsyncLocal<bool> Active = new();

    public static bool IsActive => Active.Value;

    public static IDisposable Begin()
    {
        var previous = Active.Value;
        Active.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => Active.Value = previous;
    }
}
