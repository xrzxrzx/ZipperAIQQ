using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Settings;

/// <summary>
/// 运行期设置（KV）：回复模式这类"改完要马上生效"的小配置。
/// </summary>
/// <remarks>
/// 不塞进 <c>ServerOptions</c>：那些是引导配置（改了要重启），
/// 而这里的东西要能通过 <c>/mode</c> 或 API 立即改。
/// </remarks>
public interface IRuntimeSettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, CancellationToken ct = default);
}

/// <summary>SQLite 实现。</summary>
public sealed class SqliteRuntimeSettingsStore : IRuntimeSettingsStore
{
    private readonly ISqliteDatabase _db;

    public SqliteRuntimeSettingsStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _db.ReadAsync<string?>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM RuntimeSettings WHERE Key = @key;";
            cmd.Parameters.AddWithValue("@key", key);
            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return result is null or DBNull ? null : Convert.ToString(result);
        }, ct);
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO RuntimeSettings (Key, Value)
                VALUES (@key, @value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """;
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }
}
