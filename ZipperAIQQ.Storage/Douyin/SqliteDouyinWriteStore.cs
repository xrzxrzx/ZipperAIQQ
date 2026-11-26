using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Douyin;

/// <summary><see cref="IDouyinWriteStore"/> 的 SQLite 实现（表 <c>DouyinWrites</c>，schema v21）。</summary>
public sealed class SqliteDouyinWriteStore : IDouyinWriteStore
{
    private readonly ISqliteDatabase _db;

    public SqliteDouyinWriteStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<long> AppendAsync(DouyinWriteAction action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(action.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(action.TargetKey);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO DouyinWrites (Action, TargetKey, ItemId, CommentId, Ok, Note, AtMs)
                VALUES (@action, @targetKey, @itemId, @commentId, @ok, @note, @atMs);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@action", action.Action);
            cmd.Parameters.AddWithValue("@targetKey", action.TargetKey);
            cmd.Parameters.AddWithValue("@itemId", (object?)action.ItemId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@commentId", (object?)action.CommentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ok", action.Ok ? 1 : 0);
            cmd.Parameters.AddWithValue("@note", (object?)action.Note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@atMs", action.At.ToUnixTimeMilliseconds());

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }, ct);
    }

    public Task<int> CountSinceAsync(string action, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM DouyinWrites WHERE Action = @action AND AtMs >= @sinceMs;";
            cmd.Parameters.AddWithValue("@action", action);
            cmd.Parameters.AddWithValue("@sinceMs", since.ToUnixTimeMilliseconds());

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        }, ct);
    }

    public Task<DateTimeOffset?> OldestSinceAsync(
        string action, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return _db.ReadAsync<DateTimeOffset?>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT MIN(AtMs) FROM DouyinWrites WHERE Action = @action AND AtMs >= @sinceMs;";
            cmd.Parameters.AddWithValue("@action", action);
            cmd.Parameters.AddWithValue("@sinceMs", since.ToUnixTimeMilliseconds());

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return result is null or DBNull
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(
                    Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture));
        }, ct);
    }

    public Task<DateTimeOffset?> LastAtAsync(string action, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return _db.ReadAsync<DateTimeOffset?>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(AtMs) FROM DouyinWrites WHERE Action = @action;";
            cmd.Parameters.AddWithValue("@action", action);

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return result is null or DBNull
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(
                    Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture));
        }, ct);
    }

    public Task<bool> ExistsForTargetAsync(
        string action, string targetKey, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        return _db.ReadAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM DouyinWrites
                    WHERE Action = @action AND TargetKey = @targetKey AND AtMs >= @sinceMs
                      AND Ok = 1);
                """;
            cmd.Parameters.AddWithValue("@action", action);
            cmd.Parameters.AddWithValue("@targetKey", targetKey);
            cmd.Parameters.AddWithValue("@sinceMs", since.ToUnixTimeMilliseconds());

            var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1;
        }, ct);
    }

    public Task<IReadOnlyList<DouyinWriteAction>> ListRecentAsync(
        int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 500);

        return _db.ReadAsync<IReadOnlyList<DouyinWriteAction>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, Action, TargetKey, ItemId, CommentId, Ok, Note, AtMs
                FROM DouyinWrites
                ORDER BY AtMs DESC, Id DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@limit", take);

            var list = new List<DouyinWriteAction>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new DouyinWriteAction(
                    Action: reader.GetString(1),
                    TargetKey: reader.GetString(2),
                    Ok: reader.GetInt32(5) == 1,
                    At: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                    Id: reader.GetInt64(0),
                    ItemId: reader.IsDBNull(3) ? null : reader.GetString(3),
                    CommentId: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Note: reader.IsDBNull(6) ? null : reader.GetString(6)));
            }

            return list;
        }, ct);
    }
}
