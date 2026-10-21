using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Storage.Messages;

/// <summary>
/// <see cref="IMessageStore"/> 的 SQLite 实现。
/// </summary>
/// <remarks>
/// <para>
/// 每次操作**新开一个连接**（SQLite 连接很轻，且连接池由驱动管理），
/// 不持有长连接：长连接在网络盘/休眠恢复后容易变成「僵尸连接」。
/// </para>
/// <para>
/// 全部走**参数化**，不拼 SQL 字符串 —— 消息内容来自任意群成员，属于不可信输入。
/// </para>
/// </remarks>
public sealed class SqliteMessageStore : IMessageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;

    public SqliteMessageStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<long> AppendAsync(IncomingMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = InsertSql;

        cmd.Parameters.AddWithValue("@platform", message.Chat.Platform);
        cmd.Parameters.AddWithValue("@sessionId", message.Chat.SessionId);
        cmd.Parameters.AddWithValue("@chatType", (int)message.Chat.Type);
        cmd.Parameters.AddWithValue("@chatId", message.Chat.Id);
        cmd.Parameters.AddWithValue("@messageId", (object?)message.EventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@senderUserId", message.Sender.UserId);
        cmd.Parameters.AddWithValue("@senderGroupCard", (object?)message.Sender.GroupCard ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@senderNickname", (object?)message.Sender.Nickname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@senderIsFriend", message.Sender.IsFriend ? 1 : 0);
        cmd.Parameters.AddWithValue("@plainText", message.PlainText);
        cmd.Parameters.AddWithValue("@segmentsJson", JsonSerializer.Serialize(message.Segments, JsonOptions));
        cmd.Parameters.AddWithValue("@sentAtMs", message.OccurredAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@receivedAtMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@replyToMessageId", (object?)message.ReplyToMessageId ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<StoredMessage>> GetRecentAsync(
        string sessionId, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        // 先按时间倒序取最近 N 条，再由调用方按正序使用
        cmd.CommandText = SelectRecentSql;
        cmd.Parameters.AddWithValue("@sessionId", sessionId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var buffer = new List<StoredMessage>(capacity: Math.Min(limit, 256));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            buffer.Add(ReadRow(reader));
        }

        // 倒序取 → 反转成正序
        buffer.Reverse();
        return buffer;
    }

    public async Task<long> CountAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Messages WHERE SessionId = @sessionId;";
        cmd.Parameters.AddWithValue("@sessionId", sessionId);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static StoredMessage ReadRow(SqliteDataReader reader)
    {
        var segmentsJson = reader.GetString(11);
        var segments = JsonSerializer.Deserialize<List<MessageSegment>>(segmentsJson, JsonOptions) ?? [];

        var chat = new ChatRef
        {
            Platform = reader.GetString(1),
            Type = (ChatType)reader.GetInt32(2),
            Id = reader.GetString(3),
        };

        var sender = new SenderInfo
        {
            UserId = reader.GetString(5),
            GroupCard = reader.IsDBNull(6) ? null : reader.GetString(6),
            Nickname = reader.IsDBNull(7) ? null : reader.GetString(7),
            IsFriend = reader.GetInt32(8) != 0,
        };

        var message = new IncomingMessage
        {
            Chat = chat,
            Sender = sender,
            Segments = segments,
            OccurredAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
            EventId = reader.IsDBNull(4) ? null : reader.GetString(4),
            ReplyToMessageId = reader.IsDBNull(12) ? null : reader.GetString(12),
        };

        return new StoredMessage(
            Id: reader.GetInt64(0),
            Message: message,
            ReceivedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)));
    }

    private const string InsertSql = """
        INSERT INTO Messages (
            Platform, SessionId, ChatType, ChatId, MessageId,
            SenderUserId, SenderGroupCard, SenderNickname, SenderIsFriend,
            PlainText, SegmentsJson, SentAtMs, ReceivedAtMs, ReplyToMessageId
        ) VALUES (
            @platform, @sessionId, @chatType, @chatId, @messageId,
            @senderUserId, @senderGroupCard, @senderNickname, @senderIsFriend,
            @plainText, @segmentsJson, @sentAtMs, @receivedAtMs, @replyToMessageId
        )
        RETURNING Id;
        """;

    private const string SelectRecentSql = """
        SELECT Id, Platform, ChatType, ChatId, MessageId,
               SenderUserId, SenderGroupCard, SenderNickname, SenderIsFriend,
               ReceivedAtMs, SentAtMs, SegmentsJson, ReplyToMessageId
        FROM Messages
        WHERE SessionId = @sessionId
        ORDER BY SentAtMs DESC, Id DESC
        LIMIT @limit;
        """;
}
