using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Config;

/// <summary><see cref="IApiKeyStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteApiKeyStore : IApiKeyStore
{
    private readonly ISqliteDatabase _db;

    public SqliteApiKeyStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<IReadOnlyList<StoredApiKey>> ListAsync(CancellationToken ct = default)
    {
        return _db.ReadAsync<IReadOnlyList<StoredApiKey>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Slot, KeyCipher, UpdatedAtMs
                FROM ApiKeys
                ORDER BY Slot;
                """;

            var list = new List<StoredApiKey>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new StoredApiKey(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
            }

            return list;
        }, ct);
    }

    public Task<StoredApiKey?> GetAsync(int slot, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        return _db.ReadAsync<StoredApiKey?>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Slot, KeyCipher, UpdatedAtMs
                FROM ApiKeys
                WHERE Slot = @slot;
                """;
            cmd.Parameters.AddWithValue("@slot", slot);

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            return new StoredApiKey(
                reader.GetInt32(0),
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
        }, ct);
    }

    public Task UpsertAsync(int slot, string keyCipher, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyCipher);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ApiKeys (Slot, KeyCipher, UpdatedAtMs)
                VALUES (@slot, @keyCipher, @updatedAtMs)
                ON CONFLICT(Slot) DO UPDATE SET
                    KeyCipher   = excluded.KeyCipher,
                    UpdatedAtMs = excluded.UpdatedAtMs;
                """;
            cmd.Parameters.AddWithValue("@slot", slot);
            cmd.Parameters.AddWithValue("@keyCipher", keyCipher);
            cmd.Parameters.AddWithValue("@updatedAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task DeleteAsync(int slot, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ApiKeys WHERE Slot = @slot;";
            cmd.Parameters.AddWithValue("@slot", slot);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }
}
