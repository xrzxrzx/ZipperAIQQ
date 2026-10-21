namespace ZipperAIQQ.Storage.Embeddings;

/// <summary>一条检索结果：消息主键 + 相似度分数。</summary>
public sealed record ScoredMessage(long MessageId, float Score);

/// <summary>
/// 消息向量的存储与检索。
/// </summary>
/// <remarks>
/// <para>
/// **为什么全表扫描**：单会话消息在几千条量级，512 维点积约 10ms，
/// 上 ANN 索引的收益远小于维护成本。真到十万级再换。
/// </para>
/// <para>
/// **为什么按会话过滤**：检索永远只在当前会话内做 ——
/// 把别的群的聊天记录喂给模型 = 隐私事故。
/// </para>
/// </remarks>
public interface IEmbeddingStore
{
    /// <summary>保存一条消息的向量（同 MessageId 覆盖）。</summary>
    Task SaveAsync(
        long messageId,
        string sessionId,
        ReadOnlyMemory<float> vector,
        string modelVersion,
        CancellationToken ct = default);

    /// <summary>
    /// 按相似度检索该会话内的消息。
    /// </summary>
    /// <param name="queryVector">已 L2 归一化（点积即余弦）。</param>
    /// <param name="topK">返回条数。**用 top-k 而不是阈值** —— 中文短文本无关也有 0.3 基线。</param>
    Task<IReadOnlyList<ScoredMessage>> SearchAsync(
        string sessionId,
        ReadOnlyMemory<float> queryVector,
        int topK,
        CancellationToken ct = default);

    /// <summary>取出还没算过向量的消息（用于补算）。</summary>
    Task<IReadOnlyList<long>> GetMessageIdsWithoutVectorAsync(
        string sessionId,
        int limit,
        CancellationToken ct = default);

    /// <summary>统计某会话已建向量的条数。</summary>
    Task<int> CountAsync(string sessionId, CancellationToken ct = default);
}
