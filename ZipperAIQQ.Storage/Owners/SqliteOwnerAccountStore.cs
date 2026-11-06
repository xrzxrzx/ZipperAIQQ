using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Owners;

/// <summary><see cref="IOwnerAccountStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteOwnerAccountStore : IOwnerAccountStore
{
    private readonly ISqliteDatabase _db;

    public SqliteOwnerAccountStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<IReadOnlyList<OwnerAccount>> ListAsync(CancellationToken ct = default) =>
        _db.ReadAsync<IReadOnlyList<OwnerAccount>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Platform, UserId FROM OwnerAccounts
                ORDER BY Platform;
                """;

            var list = new List<OwnerAccount>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new OwnerAccount(reader.GetString(0), reader.GetString(1)));
            }

            return list;
        }, ct);

    public Task<int> UpsertAsync(IEnumerable<OwnerAccount> accounts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var list = accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.Platform) && !string.IsNullOrWhiteSpace(a.UserId))
            .Select(a => (a.Platform, a.UserId))
            .Distinct()
            .ToList();

        return _db.WriteAsync(async (conn, token) =>
        {
            var added = 0;
            foreach (var (platform, userId) in list)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO OwnerAccounts (IdentityId, Platform, UserId)
                    VALUES (1, @platform, @userId);
                    """;
                cmd.Parameters.AddWithValue("@platform", platform);
                cmd.Parameters.AddWithValue("@userId", userId);
                added += await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            return added;
        }, ct);
    }
}
