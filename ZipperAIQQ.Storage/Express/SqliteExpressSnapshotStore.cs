using System.Globalization;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Express;

/// <summary><see cref="IExpressSnapshotStore"/> 的 SQLite 实现（表 <c>ExpressSnapshots</c>，schema v22）。</summary>
public sealed class SqliteExpressSnapshotStore : IExpressSnapshotStore
{
    /// <summary>列表上限：回滚点是几十条的东西，给多了只是把界面撑大。</summary>
    private const int MaxListLimit = 100;

    private readonly ISqliteDatabase _db;

    public SqliteExpressSnapshotStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<long> AppendAsync(
        long atMs, string actor, string payloadJson, string checksum, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksum);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ExpressSnapshots (AtMs, Actor, PayloadJson, Checksum)
                VALUES (@atMs, @actor, @payload, @checksum);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@atMs", atMs);
            cmd.Parameters.AddWithValue("@actor", actor);
            cmd.Parameters.AddWithValue("@payload", payloadJson);
            cmd.Parameters.AddWithValue("@checksum", checksum);

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }, ct);
    }

    public Task<ExpressSnapshot?> GetAsync(long id, CancellationToken ct = default)
    {
        return _db.ReadAsync<ExpressSnapshot?>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT Id, AtMs, Actor, PayloadJson, Checksum FROM ExpressSnapshots WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", id);

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            return new ExpressSnapshot(
                Id: reader.GetInt64(0),
                AtMs: reader.GetInt64(1),
                Actor: reader.GetString(2),
                PayloadJson: reader.GetString(3),
                Checksum: reader.GetString(4));
        }, ct);
    }

    public Task<IReadOnlyList<ExpressSnapshot>> ListRecentAsync(
        int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);

        return _db.ReadAsync<IReadOnlyList<ExpressSnapshot>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, AtMs, Actor, PayloadJson, Checksum
                FROM ExpressSnapshots
                ORDER BY Id DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@limit", take);

            var list = new List<ExpressSnapshot>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new ExpressSnapshot(
                    Id: reader.GetInt64(0),
                    AtMs: reader.GetInt64(1),
                    Actor: reader.GetString(2),
                    PayloadJson: reader.GetString(3),
                    Checksum: reader.GetString(4)));
            }

            return list;
        }, ct);
    }

    public Task<int> TrimAsync(int keep, CancellationToken ct = default)
    {
        // keep < 1 一律当 1：允许"只留最近一份"，但不允许把表清空
        // （清空之后连"退回上一次"都没了，而这份能力正是快照存在的理由）
        var retain = Math.Max(1, keep);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM ExpressSnapshots
                WHERE Id NOT IN (
                    SELECT Id FROM ExpressSnapshots ORDER BY Id DESC LIMIT @keep
                );
                """;
            cmd.Parameters.AddWithValue("@keep", retain);

            return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }
}
