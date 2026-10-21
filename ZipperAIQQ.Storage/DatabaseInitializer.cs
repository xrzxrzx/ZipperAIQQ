using Microsoft.Data.Sqlite;
using Serilog;

namespace ZipperAIQQ.Storage;

/// <summary>
/// 建表与 schema 版本管理。
/// </summary>
/// <remarks>
/// <para>
/// 用 <c>CREATE TABLE IF NOT EXISTS</c> + 版本号，而不是每次都 <c>DROP</c> 重建 ——
/// 消息是**用户的历史记录**，绝不能因为一次启动失败就没了。
/// </para>
/// <para>
/// M1 只有 v1。以后加表/加列时递增 <see cref="CurrentSchemaVersion"/> 并补迁移，
/// 迁移必须是**只增不改**的。
/// </para>
/// </remarks>
public sealed class DatabaseInitializer
{
    /// <summary>当前期望的 schema 版本。</summary>
    public const int CurrentSchemaVersion = 6;

    private readonly string _connectionString;
    private readonly ILogger _logger;

    public DatabaseInitializer(string connectionString, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // WAL：读写不互相阻塞（群聊高频写入时必须）
        // busy_timeout：瞬时锁冲突时等待而不是直接抛
        // foreign_keys：现在没有外键，但以后加表需要它生效
        foreach (var pragma in Pragmas)
        {
            await ExecuteAsync(conn, pragma, ct).ConfigureAwait(false);
        }

        await ExecuteAsync(conn, MessagesTable, ct).ConfigureAwait(false);
        await ExecuteAsync(conn, SessionsTable, ct).ConfigureAwait(false);
        await ExecuteAsync(conn, EmbeddingsTable, ct).ConfigureAwait(false);
        await ExecuteAsync(conn, SummariesTable, ct).ConfigureAwait(false);
        await ExecuteAsync(conn, PersonasTable, ct).ConfigureAwait(false);
        await ExecuteAsync(conn, CallMetricsTable, ct).ConfigureAwait(false);
        await ExecuteAsync(conn, SchemaVersionTable, ct).ConfigureAwait(false);

        // ── 迁移（幂等）：v1 → v2 加 Direction 列（0=收到，1=发出）──
        // 用 try/catch 而不是查 PRAGMA：重复执行时报"duplicate column"忽略即可，
        // 这样迁移逻辑不需要额外维护版本状态表。
        try
        {
            await ExecuteAsync(conn, "ALTER TABLE Messages ADD COLUMN Direction INTEGER NOT NULL DEFAULT 0;", ct).ConfigureAwait(false);
            _logger.Information("已应用迁移：Messages.Direction");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // 已迁移过
        }

        // 版本行只写一次；已有则不动
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO SchemaVersion (Version)
                SELECT @version
                WHERE NOT EXISTS (SELECT 1 FROM SchemaVersion);
                """;
            cmd.Parameters.AddWithValue("@version", CurrentSchemaVersion);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _logger.Information("数据库就绪（schema v{SchemaVersion}）", CurrentSchemaVersion);
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static readonly string[] Pragmas =
    [
        "PRAGMA journal_mode=WAL;",
        "PRAGMA busy_timeout=5000;",
        "PRAGMA foreign_keys=ON;",
    ];

    /// <summary>
    /// 消息表。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SentAtMs</c> 存 **UTC Unix 毫秒**，不存本地时间字符串 ——
    /// 时区混用是那类「看起来正常、偶尔错 8 小时」的故障（FR-15.6）。
    /// </para>
    /// <para>
    /// 唯一索引只约束**非空**的 MessageId：平台补发或历史导入时可能没有 ID，
    /// 那种消息不能因为「ID 相同（都是 null）」被拒。
    /// </para>
    /// </remarks>
    private const string MessagesTable = """
        CREATE TABLE IF NOT EXISTS Messages (
            Id               INTEGER PRIMARY KEY AUTOINCREMENT,
            Platform         TEXT    NOT NULL,
            SessionId        TEXT    NOT NULL,
            ChatType         INTEGER NOT NULL,
            ChatId           TEXT    NOT NULL,
            MessageId        TEXT,
            SenderUserId     TEXT    NOT NULL,
            SenderGroupCard  TEXT,
            SenderNickname   TEXT,
            SenderIsFriend   INTEGER NOT NULL,
            PlainText        TEXT    NOT NULL,
            SegmentsJson     TEXT    NOT NULL,
            SentAtMs         INTEGER NOT NULL,
            ReceivedAtMs     INTEGER NOT NULL,
            ReplyToMessageId TEXT
        );

        CREATE INDEX IF NOT EXISTS IX_Messages_Session_SentAt
            ON Messages (SessionId, SentAtMs);

        CREATE UNIQUE INDEX IF NOT EXISTS UX_Messages_Platform_MessageId
            ON Messages (Platform, MessageId)
            WHERE MessageId IS NOT NULL;
        """;

    /// <summary>
    /// 会话表：只存**可覆盖的会话级配置**与活跃时间。
    /// 对话内容不在这里（在 Messages），避免同一份数据两处存。
    /// </summary>
    private const string SessionsTable = """
        CREATE TABLE IF NOT EXISTS Sessions (
            SessionId      TEXT    NOT NULL PRIMARY KEY,
            Platform       TEXT    NOT NULL,
            ChatType       INTEGER NOT NULL,
            ChatId         TEXT    NOT NULL,
            PersonaId      TEXT,
            ReplyMode      TEXT,
            CreatedAtMs    INTEGER NOT NULL,
            LastActiveAtMs INTEGER NOT NULL
        );
        """;

    /// <summary>
    /// 消息向量表（M1-C 检索用）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Vector 存 BLOB</b>（float32 小端，512 维 = 2048 字节）。
    /// 用 <c>ModelVersion</c> 标记来源模型 —— 换模型后旧向量和新查询的向量**不在同一空间**，
    /// 混用会算出无意义的分数，所以必须能识别出来整批重建。
    /// </para>
    /// <para>
    /// <b>不做 ANN 索引</b>：单会话消息量在几千条量级，全表扫描 + 点积约 10ms，
    /// 引入 HNSW 之类的索引只会增加复杂度。
    /// </para>
    /// </remarks>
    private const string EmbeddingsTable = """
        CREATE TABLE IF NOT EXISTS MessageEmbeddings (
            MessageId    INTEGER NOT NULL PRIMARY KEY,
            SessionId    TEXT    NOT NULL,
            Vector       BLOB    NOT NULL,
            Dimension    INTEGER NOT NULL,
            ModelVersion TEXT    NOT NULL,
            CreatedAtMs  INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_MessageEmbeddings_Session
            ON MessageEmbeddings (SessionId);
        """;

    /// <summary>
    /// 会话摘要（M1-C 的「温层」）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 热窗只能覆盖最近几十条，再往前就丢了。全丢掉会让 AI「忘了你之前说过什么」，
    /// 全部保留又会把上下文撑爆。摘要就是中间那层：**把旧对话压成一段话**。
    /// </para>
    /// <para>
    /// <c>CoversUpToId</c> 记录「已经压到哪条消息」，避免重复压缩，也避免漏压。
    /// </para>
    /// </remarks>
    private const string SummariesTable = """
        CREATE TABLE IF NOT EXISTS SessionSummaries (
            SessionId    TEXT    NOT NULL PRIMARY KEY,
            Summary      TEXT    NOT NULL,
            CoversUpToId INTEGER NOT NULL,
            UpdatedAtMs  INTEGER NOT NULL
        );
        """;

    /// <summary>
    /// 会话级人设覆盖（M1-D）。
    /// </summary>
    /// <remarks>
    /// 只存**被覆盖**的会话；没有记录的会话回落到全局人设。
    /// 这样切人设是「写一行」，而不是「给每个会话都复制一份」。
    /// </remarks>
    private const string PersonasTable = """
        CREATE TABLE IF NOT EXISTS SessionPersonas (
            SessionId   TEXT NOT NULL PRIMARY KEY,
            Name        TEXT NOT NULL,
            Content     TEXT NOT NULL,
            UpdatedAtMs INTEGER NOT NULL
        );
        """;

/// <summary>
    /// 调用指标（耗时与 token 用量）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 每类调用记一行：LLM 调用、工具执行、向量化、检索。
    /// 客户端要做「平均耗时排序 / token 消耗」时直接查这张表。
    /// </para>
    /// <para>
    /// **只记元数据，不记内容** —— 耗时和 token 数不敏感，消息正文才敏感。
    /// </para>
    /// </remarks>
    private const string CallMetricsTable = """
        CREATE TABLE IF NOT EXISTS CallMetrics (
            Id           INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            SessionId    TEXT,
            Kind         TEXT    NOT NULL,
            ElapsedMs    REAL    NOT NULL,
            InputTokens  INTEGER,
            OutputTokens INTEGER,
            CreatedAtMs  INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_CallMetrics_Kind_Time
            ON CallMetrics (Kind, CreatedAtMs);
        """;

    private const string SchemaVersionTable = """
        CREATE TABLE IF NOT EXISTS SchemaVersion (
            Version INTEGER NOT NULL
        );
        """;
}
