using Microsoft.Extensions.AI;

namespace ZipperAIQQ.Agent.Steering;

/// <summary>
/// 把引导消息注入「下一次模型调用」的装饰器（K-3）。
/// </summary>
/// <remarks>
/// <para>
/// 挂在内层（<c>FunctionInvokingChatClient</c> 的**里面**）：工具循环每轮都会调它，
/// 所以插话能落在「工具调用全部完成后、下一轮模型调用前」——与 Pi 的 <c>steer</c> 语义一致。
/// </para>
/// <para>
/// 没有绑定队列（<see cref="SteerContext.Current"/> 为空）时就是纯透传，零开销。
/// </para>
/// </remarks>
public sealed class SteeringChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var withSteer = Inject(messages);
        return await base.GetResponseAsync(withSteer, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var withSteer = Inject(messages);
        await foreach (var update in base.GetStreamingResponseAsync(withSteer, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static List<ChatMessage> Inject(IEnumerable<ChatMessage> messages)
    {
        var list = messages as List<ChatMessage> ?? messages.ToList();

        var queue = SteerContext.Current;
        if (queue is null)
        {
            return list;
        }

        var steer = queue.Drain();
        if (steer.Count == 0)
        {
            return list;
        }

        // 追加在最后（紧邻当前输入）：模型会把它当作「刚插进来的一句话」，
        // 而不是历史里的旧消息 —— 这是引导生效的关键。
        list.Add(new ChatMessage(
            ChatRole.User,
            "【刚插进来的消息，请纳入这一轮一起考虑】" + string.Join(" / ", steer)));

        return list;
    }
}
