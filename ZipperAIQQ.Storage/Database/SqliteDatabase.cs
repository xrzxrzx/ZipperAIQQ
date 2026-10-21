using Microsoft.Data.Sqlite;
using Serilog;

namespace ZipperAIQQ.Storage.Database;

/// <summary>
/// <see cref="ISqliteDatabase"/> 的 SQLite 实现。
/// </summary>
/// <remarks>
/// <para>
/// 读操作每次开新连接（SQLite 连接很轻，连接池由驱动管理）；写操作额外经过一把
/// <see cref="SemaphoreSlim"/>，保证**应用层串行**。
/// </para>
/// <para>
/// 为什么在应用层串行、而不是只靠 SQLite 的 <c>busy_timeout</c>：
/// busy_timeout 是「撞上了就等」，等待期间**占用线程**且迟早会超时抛错。
/// 应用层串行是「根本不会撞上」，代价只是写操作排队 —— 而写操作本来就短。
/// </para>
/// <para>
/// ⚠️ 本实现只保证**进程内**串行。多进程访问同一库仍需靠 SQLite 自身的锁
/// （本项目是单实例部署，不涉及）。
/// </para>
/// </remarks>
public sealed class SqliteDatabase : ISqliteDatabase, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(initialCount: 1, maxCount: 1);
    private bool _disposed;

    public SqliteDatabase(string connectionString, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<T> ReadAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await work(conn, ct).ConfigureAwait(false);
    }

    public async Task<T> WriteAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return await work(conn, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task WriteAsync(
        Func<SqliteConnection, CancellationToken, Task> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await WriteAsync(async (conn, token) =>
        {
            await work(conn, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _writeLock.Dispose();
        _logger.Debug("统一数据库入口已释放");
        return ValueTask.CompletedTask;
    }
}
