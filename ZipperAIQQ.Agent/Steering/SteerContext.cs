namespace ZipperAIQQ.Agent.Steering;

/// <summary>
/// 「本轮从哪个引导队列取消息」的上下文（AsyncLocal）。
/// </summary>
/// <remarks>
/// 放在 AsyncLocal 而不是构造参数：<see cref="SteeringChatClient"/> 是**单例**（挂在客户端链上），
/// 而队列是**每会话每轮**的。模型调用发生在同一异步流里，AsyncLocal 正好把两者对上。
/// </remarks>
public static class SteerContext
{
    private static readonly AsyncLocal<SteerQueue?> CurrentQueue = new();

    public static SteerQueue? Current
    {
        get => CurrentQueue.Value;
        set => CurrentQueue.Value = value;
    }

    public static IDisposable Bind(SteerQueue? queue)
    {
        var previous = CurrentQueue.Value;
        CurrentQueue.Value = queue;
        return new Restore(previous);
    }

    private sealed class Restore(SteerQueue? previous) : IDisposable
    {
        public void Dispose() => CurrentQueue.Value = previous;
    }
}
