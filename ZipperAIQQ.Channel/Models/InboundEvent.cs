namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 所有**入站事件**的基类：普通消息与平台通知的共同父类。
/// </summary>
/// <remarks>
/// <para>
/// 把消息与通知放在同一个继承树的理由（FR-18.1）：它们对上层而言是同一件事 ——
/// 「平台上发生了一件事，AI 可能要对此做出反应」。若分成两套并行接口，
/// 通道实现要写两个接收循环、上层要写两个分发分支，而且以后加新事件类型时**两处都要改**。
/// </para>
/// <para>
/// ⚠️ 继承之后，<see cref="OccurredAt"/> 替代了原来消息上的 <c>SentAt</c>：
/// 「什么时候发生的」对消息和通知是同一个概念，不该有两个名字。
/// </para>
/// </remarks>
public abstract record InboundEvent
{
    /// <summary>事件发生的会话。</summary>
    public required ChatRef Chat { get; init; }

    /// <summary>
    /// 事件发生时间，取**平台时间戳**。
    /// </summary>
    /// <remarks>不要在接收时重新取 <c>UtcNow</c> —— 网络延迟与补发消息会让两者差出可观时间（FR-15.1）。</remarks>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>平台事件 ID（用于去重）。通知类事件可能没有。</summary>
    public string? EventId { get; init; }
}
