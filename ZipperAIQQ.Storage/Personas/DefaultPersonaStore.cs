using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Personas;

/// <summary>
/// 人设存储：会话级查表，全局回落文件。
/// </summary>
/// <remarks>
/// <para>
/// **全局人设不走数据库**：它是部署时确定的一份文件（<c>config/persona.md</c>）。
/// </para>
/// <para>
/// ⚠️ 但它**必须可重载** —— 客户端能改全局人设了（<c>setGlobalPersona</c>），
/// 如果这里把文件内容钉死在构造函数里，改完文件后再读还是旧值
/// （实测表现：客户端保存人设成功，重新读取又变回默认）。
/// </para>
/// </remarks>
public sealed class DefaultPersonaStore : IPersonaStore
{
    private readonly ISqliteDatabase _db;

    /// <summary>全局人设（可变：文件被改后由 <see cref="ReloadGlobal"/> 刷新）。</summary>
    private Persona _global;

    public DefaultPersonaStore(ISqliteDatabase db, Persona global)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(global);

        _db = db;
        _global = global;
    }

    /// <summary>
    /// 用新内容替换内存里的全局人设。
    /// </summary>
    /// <remarks>
    /// 由 <c>setGlobalPersona</c> 写完文件后调用 —— 保证「写完立刻能读到」，
    /// 不用等重启。
    /// </remarks>
    public void ReloadGlobal(Persona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        _global = persona;
    }

    public async Task<Persona> GetAsync(
        string platform, string chatType, string chatId, CancellationToken ct = default)
    {
        var sessionId = BuildSessionId(platform, chatType, chatId);
        var state = await GetStateAsync(sessionId, ct).ConfigureAwait(false);

        // 会话级预设被关掉时，等同于"没有覆盖" —— 回落到全局（#16）
        return state.Override is not null && state.Enabled ? state.Override : _global;
    }

    public Task<PersonaState> GetStateAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _db.ReadAsync<PersonaState>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Name, Content, Enabled FROM SessionPersonas WHERE SessionId = @sid;";
            cmd.Parameters.AddWithValue("@sid", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return new PersonaState(null, true);
            }

            return new PersonaState(
                new Persona(reader.GetString(0), reader.GetString(1)),
                reader.GetInt32(2) != 0);
        }, ct);
    }

    public async Task<bool> IsEnabledAsync(string sessionId, CancellationToken ct = default)
    {
        var state = await GetStateAsync(sessionId, ct).ConfigureAwait(false);
        return state.Enabled;
    }

    public Task SetEnabledAsync(string sessionId, bool enabled, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE SessionPersonas SET Enabled = @enabled WHERE SessionId = @sid;";
            cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
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

    /// <summary>
    /// 与 <c>ChatRef.SessionId</c> **必须同构**，否则会话级人设会查不到。
    /// 这里不引 Channel 依赖，所以自己拼一次 —— 两处一起改。
    /// </summary>
    private static string BuildSessionId(string platform, string chatType, string chatId) =>
        $"{platform}:{(chatType == "Group" ? "g" : "p")}{chatId}";
}
