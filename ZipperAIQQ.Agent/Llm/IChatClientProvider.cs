using Microsoft.Extensions.AI;

namespace ZipperAIQQ.Agent.Llm;

/// <summary>
/// 提供"当前生效"的 LLM 客户端；配置变更后可**热替换**（FR-8.4）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不直接注入 <see cref="IChatClient"/>：那是个不可变单例，
/// 换端点 / 换模型 / 换 key 都只能重启。
/// </para>
/// <para>
/// <see cref="Current"/> 每次取都拿最新的；旧客户端在替换时释放。
/// </para>
/// </remarks>
public interface IChatClientProvider
{
    /// <summary>是否已有可用客户端（未配置时为 false）。</summary>
    bool HasClient { get; }

    /// <summary>当前客户端。未初始化时抛异常 —— 调用方应先用 <see cref="HasClient"/> 判断。</summary>
    IChatClient Current { get; }

    /// <summary>
    /// 按最新配置重建；**失败时保留旧客户端**（不能因为一次配置写错就把对话能力弄没）。
    /// </summary>
    Task ReloadAsync(CancellationToken ct = default);
}
