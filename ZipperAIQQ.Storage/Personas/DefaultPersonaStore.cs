using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Personas;

/// <summary>
/// 人设存储：会话级查表，全局回落文件。
/// </summary>
/// <remarks>
/// **全局人设不走数据库**：它是部署时确定的一份，放文件里改完重启即可，
/// 没必要为了"能改"引入一套管理界面。等客户端要编辑它时再考虑迁移。
/// </remarks>
public sealed class DefaultPersonaStore : IPersonaStore
{
    private readonly ISqliteDatabase _db;
    private readonly Persona _global;

    public DefaultPersonaStore(ISqliteDatabase db, Persona global)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(global);

        _db = db;
        _global = global;
    }

    public async Task<Persona> GetAsync(
        string platform, string chatType, string chatId, CancellationToken ct = default)
    {
        var sessionId = BuildSessionId(platform, chatType, chatId);
        var overridePersona = await GetOverrideAsync(sessionId, ct).ConfigureAwait(false);
        return overridePersona ?? _global;
    }

    public Task SetAsync(string sessionId, Persona persona, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(persona);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SessionPersonas (SessionId, Name, Content, UpdatedAtMs)
                VALUES (@sid, @name, @content, @now)
                ON CONFLICT(SessionId) DO UPDATE SET
                    Name        = excluded.Name,
                    Content     = excluded.Content,
                    UpdatedAtMs = excluded.UpdatedAtMs;
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@name", persona.Name);
            cmd.Parameters.AddWithValue("@content", persona.Content);
            cmd.Parameters.AddWithValue("@now", now);

            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task ClearAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM SessionPersonas WHERE SessionId = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    private Task<Persona?> GetOverrideAsync(string sessionId, CancellationToken ct) =>
        _db.ReadAsync<Persona?>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Name, Content FROM SessionPersonas WHERE SessionId = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return null;
            }

            return new Persona(reader.GetString(0), reader.GetString(1));
        }, ct);

    /// <summary>
    /// 与 <c>ChatRef.SessionId</c> **必须同构**，否则会话级人设会查不到。
    /// 这里不引 Channel 依赖，所以自己拼一次 —— 两处一起改。
    /// </summary>
    private static string BuildSessionId(string platform, string chatType, string chatId) =>
        $"{platform}:{(chatType == "Group" ? "g" : "p")}{chatId}";
}
