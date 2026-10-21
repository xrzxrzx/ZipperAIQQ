using Microsoft.Data.Sqlite;
using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Config;

/// <summary><see cref="IConfigStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteConfigStore : IConfigStore
{
    private readonly ISqliteDatabase _db;

    public SqliteConfigStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<IReadOnlyList<ModelEndpointSetting>> ListEndpointsAsync(CancellationToken ct = default)
    {
        return _db.ReadAsync<IReadOnlyList<ModelEndpointSetting>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Name, BaseUrl, Model, ApiKind, KeyCipher, UpdatedAtMs
                FROM ModelEndpoints
                ORDER BY Name;
                """;

            var list = new List<ModelEndpointSetting>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(ReadEndpoint(reader));
            }

            return list;
        }, ct);
    }

    public Task<ModelEndpointSetting?> GetEndpointAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _db.ReadAsync<ModelEndpointSetting?>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Name, BaseUrl, Model, ApiKind, KeyCipher, UpdatedAtMs
                FROM ModelEndpoints
                WHERE Name = @name;
                """;
            cmd.Parameters.AddWithValue("@name", name);

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadEndpoint(reader) : null;
        }, ct);
    }

    public Task UpsertEndpointAsync(ModelEndpointSetting endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ModelEndpoints (Name, BaseUrl, Model, ApiKind, KeyCipher, UpdatedAtMs)
                VALUES (@name, @baseUrl, @model, @apiKind, @keyCipher, @updatedAtMs)
                ON CONFLICT(Name) DO UPDATE SET
                    BaseUrl     = excluded.BaseUrl,
                    Model       = excluded.Model,
                    ApiKind     = excluded.ApiKind,
                    KeyCipher   = COALESCE(excluded.KeyCipher, ModelEndpoints.KeyCipher),
                    UpdatedAtMs = excluded.UpdatedAtMs;
                """;

            cmd.Parameters.AddWithValue("@name", endpoint.Name);
            cmd.Parameters.AddWithValue("@baseUrl", endpoint.BaseUrl);
            cmd.Parameters.AddWithValue("@model", endpoint.Model);
            cmd.Parameters.AddWithValue("@apiKind", endpoint.ApiKind);
            // key 没给（null）时**保留旧密文** —— 不能因为只改模型名就把 key 抹掉
            cmd.Parameters.AddWithValue("@keyCipher", (object?)endpoint.KeyCipher ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@updatedAtMs", endpoint.UpdatedAt.ToUnixTimeMilliseconds());

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task DeleteEndpointAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ModelEndpoints WHERE Name = @name;";
            cmd.Parameters.AddWithValue("@name", name);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<RoleBinding>> ListRolesAsync(CancellationToken ct = default)
    {
        return _db.ReadAsync<IReadOnlyList<RoleBinding>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Role, EndpointName FROM RoleBindings ORDER BY Role;";

            var list = new List<RoleBinding>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new RoleBinding(reader.GetString(0), reader.GetString(1)));
            }

            return list;
        }, ct);
    }

    public Task SetRoleAsync(string role, string endpointName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO RoleBindings (Role, EndpointName)
                VALUES (@role, @endpoint)
                ON CONFLICT(Role) DO UPDATE SET EndpointName = excluded.EndpointName;
                """;
            cmd.Parameters.AddWithValue("@role", role);
            cmd.Parameters.AddWithValue("@endpoint", endpointName);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task AppendAuditAsync(string action, string actor, string? target, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO AuditLog (Action, Actor, Target, AtMs)
                VALUES (@action, @actor, @target, @atMs);
                """;
            cmd.Parameters.AddWithValue("@action", action);
            cmd.Parameters.AddWithValue("@actor", actor);
            cmd.Parameters.AddWithValue("@target", (object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@atMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<AuditEntry>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Action, Actor, Target, AtMs
                FROM AuditLog
                ORDER BY Id DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = new List<AuditEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new AuditEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
            }

            return list;
        }, ct);
    }

    private static ModelEndpointSetting ReadEndpoint(SqliteDataReader reader) =>
        new(
            Name: reader.GetString(0),
            BaseUrl: reader.GetString(1),
            Model: reader.GetString(2),
            ApiKind: reader.GetInt32(3),
            KeyCipher: reader.IsDBNull(4) ? null : reader.GetString(4),
            UpdatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)));
}
