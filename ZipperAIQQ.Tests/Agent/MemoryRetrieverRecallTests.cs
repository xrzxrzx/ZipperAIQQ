using Serilog;
using ZipperAIQQ.Agent.Embedding;
using ZipperAIQQ.Agent.Memory;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Embeddings;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>R-09 评估后的替代方案：跨会话召回 + 混合打分。</summary>
public sealed class MemoryRetrieverRecallTests
{
    private sealed class FakeEmbedding : IEmbeddingService
    {
        public int Dimension => 2;
        public ReadOnlyMemory<float> Embed(string text) => new float[] { 1f, 0f };
    }

    private sealed class FakeVectors(List<ScoredSessionMessage> hits) : IEmbeddingStore
    {
        public string? LastExclude { get; private set; }

        public Task<IReadOnlyList<ScoredSessionMessage>> SearchAcrossSessionsAsync(
            ReadOnlyMemory<float> queryVector, int topK, string? excludeSessionId = null,
            CancellationToken ct = default)
        {
            LastExclude = excludeSessionId;
            return Task.FromResult<IReadOnlyList<ScoredSessionMessage>>(
                hits.Where(h => h.SessionId != excludeSessionId).Take(topK).ToList());
        }

        public Task SaveAsync(long messageId, string sessionId, ReadOnlyMemory<float> vector,
            string modelVersion, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(long messageId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<ScoredMessage>> SearchAsync(string sessionId,
            ReadOnlyMemory<float> queryVector, int topK, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScoredMessage>>([]);

        public Task<IReadOnlyList<PendingEmbedding>> GetPendingAsync(string sessionId, int limit,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PendingEmbedding>>([]);

        public Task<int> CountAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeMessages(Dictionary<long, StoredMessage> rows) : IMessageStore
    {
        public Task<IReadOnlyList<StoredMessage>> GetByIdsAsync(string sessionId,
            IReadOnlyList<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(
                ids.Where(rows.ContainsKey).Select(id => rows[id]).ToList());

        public Task<long> AppendAsync(IncomingMessage message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> AppendOutgoingAsync(OutgoingMessage message, string senderUserId, DateTimeOffset sentAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> AppendCommandAsync(OutgoingMessage message, string senderUserId, DateTimeOffset sentAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredMessage>> GetRecentAsync(string sessionId, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredMessage>> GetRangeAsync(string sessionId, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StoredMessage?> FindByMessageIdAsync(string sessionId, string messageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> CountAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<StoredMessage>> SearchAsync(string sessionId, string keyword, int limit, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<StoredMessage>> SearchAcrossSessionsAsync(string keyword, int limit, string? platform, string? sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgeAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NoopMetrics : IMetricsStore
    {
        public Task RecordAsync(MetricEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MetricSummary>> GetSummaryAsync(int windowHours = 24, string? sessionId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MetricSummary>>([]);
        public Task<IReadOnlyList<MetricSessionInfo>> ListSessionAsync(int windowHours = 24, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MetricSessionInfo>>([]);
    }

    private static StoredMessage Msg(long id, string chatId, string text, DateTimeOffset at) => new(
        id,
        new IncomingMessage
        {
            Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = chatId },
            Sender = new SenderInfo { UserId = "1", Nickname = "拉链" },
            Segments = [new TextSegment(text)],
            OccurredAt = at,
        },
        at,
        IsOutgoing: false,
        IsCommand: false);

    [Fact]
    public async Task Keyword_overlap_boosts_a_slightly_less_similar_hit()
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new Dictionary<long, StoredMessage>
        {
            [1] = Msg(1, "1", "提醒我十一点要睡觉", now.AddDays(-1)),
            [2] = Msg(2, "2", "今天天气不错", now.AddDays(-1)),
        };

        var vectors = new FakeVectors([
            new ScoredSessionMessage("qq:p1", 1, 0.60f),
            new ScoredSessionMessage("qq:p2", 2, 0.65f),
        ]);

        var retriever = new MemoryRetriever(
            new FakeEmbedding(), vectors, new FakeMessages(rows), new NoopMetrics(),
            new LoggerConfiguration().CreateLogger());

        var hits = await retriever.RecallAcrossSessionsAsync("qq:p0", "睡觉提醒", topK: 2);

        Assert.Equal(1, hits[0].Message.Id);   // 关键词命中把 0.60 顶到 0.65 之前
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public async Task Current_session_is_excluded()
    {
        var vectors = new FakeVectors([
            new ScoredSessionMessage("qq:p1", 1, 0.9f),
            new ScoredSessionMessage("qq:p2", 2, 0.8f),
        ]);
        var rows = new Dictionary<long, StoredMessage>
        {
            [1] = Msg(1, "1", "本会话", DateTimeOffset.UtcNow),
            [2] = Msg(2, "2", "别的会话", DateTimeOffset.UtcNow),
        };

        var retriever = new MemoryRetriever(
            new FakeEmbedding(), vectors, new FakeMessages(rows), new NoopMetrics(),
            new LoggerConfiguration().CreateLogger());

        var hits = await retriever.RecallAcrossSessionsAsync("qq:p1", "本会话", topK: 5);

        Assert.Single(hits);
        Assert.Equal("qq:p2", hits[0].Message.Message.Chat.SessionId);
    }
}
