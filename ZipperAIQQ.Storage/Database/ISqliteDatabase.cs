using Microsoft.Data.Sqlite;

namespace ZipperAIQQ.Storage.Database;

/// <summary>
/// 数据库访问的**唯一入口**（FR-16）。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：**一个聊天一个 agent 实例**意味着多个实例会同时写同一个 SQLite 库。
/// 如果每个仓储各开各的连接，锁冲突会变成**随机偶发的 `database is locked`** ——
/// 而且测试几乎复现不出来。
/// </para>
/// <para>
/// 把并发点收成**一个**，才管得住：写操作在应用层串行，读操作放行并发。
/// </para>
/// <para>
/// ⚠️ <b>不要在 <see cref="WriteAsync{T}"/> 内部再调用 <see cref="WriteAsync{T}"/></b> ——
/// 信号量不可重入，会**死锁**。需要组合多个写操作时，在同一个 work 委托里做完。
/// </para>
/// </remarks>
public interface ISqliteDatabase
{
    /// <summary>读操作：**可并发**。传入已打开的连接，实现负责其生命周期。</summary>
    Task<T> ReadAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> work,
        CancellationToken ct = default);

    /// <summary>写操作：**串行**。相同 work 内的多条语句应在同一事务里完成。</summary>
    Task<T> WriteAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> work,
        CancellationToken ct = default);

    /// <summary>写操作（无返回值）。</summary>
    Task WriteAsync(
        Func<SqliteConnection, CancellationToken, Task> work,
        CancellationToken ct = default);
}
