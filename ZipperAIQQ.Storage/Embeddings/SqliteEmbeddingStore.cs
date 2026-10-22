using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Embeddings;

/// <summary>
/// <see cref="IEmbeddingStore"/> 的 SQLite 实现。
/// </summary>
/// <remarks>
/// <para>
/// 向量存 BLOB（float32 小端）。读出来在**内存里算点积并排序** ——
/// SQLite 没有向量运算，在 SQL 里算不了余弦；而单会话数据量小，全扫完全够用。
/// </para>
/// <para>
/// 一律走 <see cref="ISqliteDatabase"/>，不自建连接（与消息仓储同一纪律）。
/// </para>
/// </remarks>
public sealed class SqliteEmbeddingStore : IEmbeddingStore
{
    private readonly ISqliteDatabase _db;

    public SqliteEmbeddingStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task SaveAsync(
        long messageId,
        string sessionId,
        ReadOnlyMemory<float> vector,
        string modelVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);

        var blob = ToBlob(vector);
        var dimension = vector.Length;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO MessageEmbeddings (MessageId, SessionId, Vector, Dimension, ModelVersion, CreatedAtMs)
                VALUES (@id, @sid, @vec, @dim, @ver, @now)
                ON CONFLICT(MessageId) DO UPDATE SET
                    SessionId    = excluded.SessionId,
                    Vector       = excluded.Vector,
                    Dimension    = excluded.Dimension,
                    ModelVersion = excluded.ModelVersion;
                """;
            cmd.Parameters.AddWithValue("@id", messageId);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@vec", blob);
            cmd.Parameters.AddWithValue("@dim", dimension);
            cmd.Parameters.AddWithValue("@ver", modelVersion);
            cmd.Parameters.AddWithValue("@now", now);

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<ScoredMessage>> SearchAsync(
        string sessionId,
        ReadOnlyMemory<float> queryVector,
        int topK,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var limit = Math.Max(1, topK);

        return _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MessageId, Vector FROM MessageEmbeddings WHERE SessionId = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);

            var scored = new List<ScoredMessage>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var messageId = reader.GetInt64(0);
                var blob = (byte[])reader["Vector"];
                var vector = FromBlob(blob);

                // 已归一化 → 点积即余弦
                scored.Add(new ScoredMessage(messageId, Dot(queryVector.Span, vector)));
            }

            return (IReadOnlyList<ScoredMessage>)scored
                .OrderByDescending(s => s.Score)
                .Take(limit)
                .ToList();
        }, ct);
    }

    public Task<IReadOnlyList<PendingEmbedding>> GetPendingAsync(
        string sessionId,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var take = Math.Max(1, limit);

        return _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT m.Id, m.PlainText
                FROM Messages m
                LEFT JOIN MessageEmbeddings e ON e.MessageId = m.Id
                WHERE m.SessionId = @sid
                  AND e.MessageId IS NULL
                  AND m.PlainText <> ''
                ORDER BY m.Id ASC
                LIMIT @take;
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@take", take);

            var pending = new List<PendingEmbedding>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                pending.Add(new PendingEmbedding(reader.GetInt64(0), reader.GetString(1)));
            }

            return (IReadOnlyList<PendingEmbedding>)pending;
        }, ct);
    }

    public Task<int> CountAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM MessageEmbeddings WHERE SessionId = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        }, ct);
    }

    public Task<bool> DeleteAsync(long messageId, CancellationToken ct = default)
    {
        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM MessageEmbeddings WHERE MessageId = @id;";
            cmd.Parameters.AddWithValue("@id", messageId);
            return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
        }, ct);
    }

    private static byte[] ToBlob(ReadOnlyMemory<float> vector) =>
        MemoryMarshal.AsBytes(vector.Span).ToArray();

    private static float[] FromBlob(byte[] blob)
    {
        var floats = new float[blob.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(blob.AsSpan()).CopyTo(floats);
        return floats;
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var length = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < length; i++)
        {
            sum += (double)a[i] * b[i];
        }
        return (float)sum;
    }
}
