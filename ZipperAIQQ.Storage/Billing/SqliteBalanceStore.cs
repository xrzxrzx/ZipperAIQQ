using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Billing;

/// <summary><see cref="IBalanceStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteBalanceStore : IBalanceStore
{
    private readonly ISqliteDatabase _db;

    public SqliteBalanceStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task AppendAsync(BalanceSample sample, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO BalanceSamples
                    (Provider, Currency, Total, Granted, ToppedUp, IsAvailable, SampledAtMs)
                VALUES (@provider, @currency, @total, @granted, @toppedUp, @available, @at);
                """;
            cmd.Parameters.AddWithValue("@provider", sample.Provider);
            cmd.Parameters.AddWithValue("@currency", sample.Currency);
            cmd.Parameters.AddWithValue("@total", (double)sample.Total);
            cmd.Parameters.AddWithValue("@granted", (double)sample.Granted);
            cmd.Parameters.AddWithValue("@toppedUp", (double)sample.ToppedUp);
            cmd.Parameters.AddWithValue("@available", sample.IsAvailable ? 1 : 0);
            cmd.Parameters.AddWithValue("@at", sample.SampledAt.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<BalanceSample>> ListSinceAsync(
        string provider, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        return _db.ReadAsync<IReadOnlyList<BalanceSample>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Provider, Currency, Total, Granted, ToppedUp, IsAvailable, SampledAtMs
                FROM BalanceSamples
                WHERE Provider = @provider AND SampledAtMs >= @sinceMs
                ORDER BY SampledAtMs;
                """;
            cmd.Parameters.AddWithValue("@provider", provider);
            cmd.Parameters.AddWithValue("@sinceMs", since.ToUnixTimeMilliseconds());

            var list = new List<BalanceSample>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(Read(reader));
            }

            return list;
        }, ct);
    }

    public Task<BalanceSample?> LatestAsync(string provider, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        return _db.ReadAsync<BalanceSample?>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Provider, Currency, Total, Granted, ToppedUp, IsAvailable, SampledAtMs
                FROM BalanceSamples
                WHERE Provider = @provider
                ORDER BY SampledAtMs DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("@provider", provider);

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? Read(reader) : null;
        }, ct);
    }

    public Task<int> PurgeBeforeAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM BalanceSamples WHERE SampledAtMs < @beforeMs;";
            cmd.Parameters.AddWithValue("@beforeMs", before.ToUnixTimeMilliseconds());
            return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    private static BalanceSample Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        (decimal)reader.GetDouble(2),
        (decimal)reader.GetDouble(3),
        (decimal)reader.GetDouble(4),
        reader.GetInt64(5) != 0,
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)));
}
