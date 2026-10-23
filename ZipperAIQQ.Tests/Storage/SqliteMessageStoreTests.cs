using Microsoft.Data.Sqlite;
using Serilog;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>
/// 消息存储：落库 / 查询 / 跨会话搜索 / 清理。
/// </summary>
/// <remarks>
/// 用**临时文件**库而不是 <c>:memory:</c>：<see cref="SqliteDatabase"/> 每次读写都开新连接，
/// 内存库的生命周期跟着连接走，关掉就没了。
/// </remarks>
public sealed class SqliteMessageStoreTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteMessageStore _store;

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    public SqliteMessageStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-test-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _store = new SqliteMessageStore(_db);
    }

    public async Task InitializeAsync()
    {
        var initializer = new DatabaseInitializer(_connectionString, Logger);
        await initializer.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        // 连接池可能还持有句柄，忽略删除失败
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private static IncomingMessage Message(string text, string chatId = "10001", ChatType type = ChatType.Private)
        => new()
        {
            Chat = new ChatRef { Platform = "qq", Type = type, Id = chatId },
            Sender = new SenderInfo { UserId = "10001", Nickname = "测试" },
            Segments = [new TextSegment(text)],
            EventId = Guid.NewGuid().ToString("N"),
            OccurredAt = DateTimeOffset.UtcNow,
        };

    // ─────────────────────────── 写入 / 读取 ───────────────────────────

    [Fact]
    public async Task Append_then_recent_returns_message()
    {
        var msg = Message("你好");
        await _store.AppendAsync(msg);

        var recent = await _store.GetRecentAsync(msg.Chat.SessionId, 10);

        var single = Assert.Single(recent);
        Assert.Equal("你好", single.Message.PlainText);
        Assert.False(single.IsOutgoing);
    }

    [Fact]
    public async Task Recent_is_capped_by_limit()
    {
        var chat = TestData.PrivateChat();
        for (var i = 0; i < 5; i++)
        {
            await _store.AppendAsync(Message($"第{i}条"));
        }

        var recent = await _store.GetRecentAsync(chat.SessionId, 3);

        Assert.Equal(3, recent.Count);
    }

    [Fact]
    public async Task Sessions_are_isolated()
    {
        await _store.AppendAsync(Message("A 会话", chatId: "111"));
        await _store.AppendAsync(Message("B 会话", chatId: "222"));

        var a = await _store.GetRecentAsync("qq:p111", 10);
        var b = await _store.GetRecentAsync("qq:p222", 10);

        Assert.Equal("A 会话", Assert.Single(a).Message.PlainText);
        Assert.Equal("B 会话", Assert.Single(b).Message.PlainText);
    }

    // ─────────────────────────── 搜索 ───────────────────────────

    [Fact]
    public async Task Search_finds_keyword_in_same_session_only()
    {
        await _store.AppendAsync(Message("今天吃了拉面", chatId: "111"));
        await _store.AppendAsync(Message("今天吃了米饭", chatId: "222"));

        var hits = await _store.SearchAsync("qq:p111", "拉面", 10);

        Assert.Single(hits);
        Assert.Contains("拉面", hits[0].Message.PlainText);
    }

    [Fact]
    public async Task Search_returns_empty_for_unknown_keyword()
    {
        await _store.AppendAsync(Message("随便聊聊"));

        Assert.Empty(await _store.SearchAsync("qq:p10001", "完全不存在的词", 10));
    }

    // ─────────────────────────── 跨会话搜索（第 F 项）───────────────────────────

    [Fact]
    public async Task Cross_session_search_finds_other_sessions()
    {
        await _store.AppendAsync(Message("我说了「复活」这个词", chatId: "111"));

        var hits = await _store.SearchAcrossSessionsAsync("复活", 10);

        Assert.Single(hits);
        Assert.Contains("复活", hits[0].Message.PlainText);
    }

    [Fact]
    public async Task Cross_session_search_excludes_current_session()
    {
        // 排除当前会话是关键：否则自己这边的对话会被重复算进"别的会话"
        await _store.AppendAsync(Message("本会话提到「复活」", chatId: "111"));
        await _store.AppendAsync(Message("别的会话也提到「复活」", chatId: "222"));

        var hits = await _store.SearchAcrossSessionsAsync("复活", 10, excludeSessionId: "qq:p111");

        Assert.Single(hits);
        Assert.Contains("别的会话", hits[0].Message.PlainText);
    }

    [Fact]
    public async Task Cross_session_search_respects_limit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _store.AppendAsync(Message($"含关键词句子{i}", chatId: $"9{i}"));
        }

        var hits = await _store.SearchAcrossSessionsAsync("关键词", 2);

        Assert.Equal(2, hits.Count);
    }

    // ─────────────────────────── 会话列表 / 清理 ───────────────────────────

    [Fact]
    public async Task List_sessions_reports_counts()
    {
        await _store.AppendAsync(Message("一", chatId: "111"));
        await _store.AppendAsync(Message("二", chatId: "111"));
        await _store.AppendAsync(Message("三", chatId: "222"));

        var sessions = await _store.ListSessionsAsync(10);

        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, s => s.SessionId == "qq:p111" && s.MessageCount == 2);
    }

    [Fact]
    public async Task Purge_removes_only_target_session()
    {
        await _store.AppendAsync(Message("要删的", chatId: "111"));
        await _store.AppendAsync(Message("留着的", chatId: "222"));

        var deleted = await _store.PurgeAsync("qq:p111");

        Assert.Equal(1, deleted);
        Assert.Empty(await _store.GetRecentAsync("qq:p111", 10));
        Assert.Single(await _store.GetRecentAsync("qq:p222", 10));
    }

    [Fact]
    public async Task Count_matches_appended()
    {
        for (var i = 0; i < 3; i++)
        {
            await _store.AppendAsync(Message($"第{i}条"));
        }

        Assert.Equal(3, await _store.CountAsync("qq:p10001"));
    }
}
