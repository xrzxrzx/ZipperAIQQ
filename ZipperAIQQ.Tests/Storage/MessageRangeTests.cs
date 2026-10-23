using Serilog;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>
/// 按时间区间取消息（#37 会话导出的数据源）。
/// </summary>
public sealed class MessageRangeTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly SqliteMessageStore _store;
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    public MessageRangeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-range-{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase($"Data Source={_dbPath}", Logger);
        _store = new SqliteMessageStore(_db);
    }

    public async Task InitializeAsync()
        => await new DatabaseInitializer($"Data Source={_dbPath}", Logger).InitializeAsync();

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private Task<long> AppendAsync(string text, DateTimeOffset at, string sessionId = "qq:p1")
        => _store.AppendAsync(new IncomingMessage
        {
            Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = sessionId.Split(':')[1][1..] },
            Sender = new SenderInfo { UserId = "1", Nickname = "测试" },
            Segments = [new TextSegment(text)],
            OccurredAt = at,
            EventId = Guid.NewGuid().ToString("N"),
        });

    [Fact]
    public async Task Range_includes_both_endpoints()
    {
        var day = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        await AppendAsync("前一天", day.AddDays(-1));
        await AppendAsync("第一天早上", day.AddHours(8));
        await AppendAsync("第二天晚上", day.AddDays(1).AddHours(23));
        await AppendAsync("第三天", day.AddDays(2).AddHours(1));

        var rows = await _store.GetRangeAsync(
            "qq:p1",
            day,
            day.AddDays(1).AddHours(23).AddMilliseconds(999),
            100);

        Assert.Equal(2, rows.Count);
        Assert.Equal("第一天早上", rows[0].Message.PlainText);
        Assert.Equal("第二天晚上", rows[1].Message.PlainText);
    }

    [Fact]
    public async Task Range_without_bounds_returns_everything_in_order()
    {
        var day = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        await AppendAsync("第二条", day.AddMinutes(2));
        await AppendAsync("第一条", day.AddMinutes(1));

        var rows = await _store.GetRangeAsync("qq:p1", null, null, 100);

        Assert.Equal(["第一条", "第二条"], rows.Select(r => r.Message.PlainText).ToArray());
    }

    [Fact]
    public async Task Range_is_isolated_per_session()
    {
        var day = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        await AppendAsync("会话一", day, "qq:p1");
        await AppendAsync("会话二", day, "qq:p2");

        var rows = await _store.GetRangeAsync("qq:p1", null, null, 100);

        Assert.Single(rows);
        Assert.Equal("会话一", rows[0].Message.PlainText);
    }
}
