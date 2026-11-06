using Microsoft.Extensions.AI;
using ZipperAIQQ.Agent.Steering;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>K-3：引导消息注入下一次模型调用。</summary>
public sealed class SteeringChatClientTests
{
    private sealed class CapturingClient : IChatClient
    {
        public List<ChatMessage> LastMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages.Clear();
            LastMessages.AddRange(messages);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Queued_text_is_appended_to_next_model_call()
    {
        var inner = new CapturingClient();
        var client = new SteeringChatClient(inner);
        var queue = new SteerQueue();
        queue.Enqueue("先别急着定，我补充一句");

        using (SteerContext.Bind(queue))
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "原消息")]);
        }

        Assert.Equal(2, inner.LastMessages.Count);
        Assert.Contains("先别急着定", inner.LastMessages[^1].Text);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Empty_queue_is_pure_passthrough()
    {
        var inner = new CapturingClient();
        var client = new SteeringChatClient(inner);

        using (SteerContext.Bind(new SteerQueue()))
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "只有原消息")]);
        }

        Assert.Single(inner.LastMessages);
    }

    [Fact]
    public async Task Without_context_nothing_is_injected()
    {
        var inner = new CapturingClient();
        var client = new SteeringChatClient(inner);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "无上下文")]);

        Assert.Single(inner.LastMessages);
    }
}
