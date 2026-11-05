using ZipperAIQQ.Storage.Database;

namespace ZipperAIQQ.Storage.Schedules;

/// <summary><see cref="IScheduleExecutionStore"/> 的 SQLite 实现。</summary>
public sealed class SqliteScheduleExecutionStore : IScheduleExecutionStore
{
    private readonly ISqliteDatabase _db;

    public SqliteScheduleExecutionStore(ISqliteDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task AppendAsync(ScheduleExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return _db.WriteAsync(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ScheduleExecutions (TaskId, Outcome, Detail, CreatedAtMs)
                VALUES (@taskId, @outcome, @detail, @createdAtMs);
                """;
            cmd.Parameters.AddWithValue("@taskId", execution.TaskId);
            cmd.Parameters.AddWithValue("@outcome", execution.Outcome);
            cmd.Parameters.AddWithValue("@detail", (object?)execution.Detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAtMs", execution.CreatedAt.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, ct);
    }

    public Task<IReadOnlyList<ScheduleExecution>> ListByTaskAsync(
        long taskId, int limit = 20, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return _db.ReadAsync<IReadOnlyList<ScheduleExecution>>(async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, TaskId, Outcome, Detail, CreatedAtMs
                FROM ScheduleExecutions
                WHERE TaskId = @taskId
                ORDER BY Id DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@taskId", taskId);
            cmd.Parameters.AddWithValue("@limit", limit);

            var list = new List<ScheduleExecution>();
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                list.Add(new ScheduleExecution(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
            }

            return list;
        }, ct);
    }
}
