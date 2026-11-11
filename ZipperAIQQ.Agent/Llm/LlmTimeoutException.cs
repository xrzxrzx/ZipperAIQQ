namespace ZipperAIQQ.Agent.Llm;

/// <summary>模型调用超过本地上限（不是调用方取消）。</summary>
public sealed class LlmTimeoutException(int seconds)
    : TimeoutException($"模型调用超过 {seconds} 秒，已中止本轮")
{
    public int TimeoutSeconds { get; } = seconds;
}
