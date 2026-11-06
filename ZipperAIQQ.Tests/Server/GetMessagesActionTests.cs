using System.Text.Json;
using Serilog;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Api.Handlers;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Tests.Server;

/// <summary>H-6：「未回复」只标在没被名单拦下的消息上。</summary>
public sealed class GetMessagesActionTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;
    private readonly SqliteDatabase _db;
    private readonly SqliteMessageStore _store;

    public GetMessagesActionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-getmsg-{Guid.NewGuid():N}.db");
        _db = new SqliteDatabase($"Data Source={_dbPath}", Logger);
        _store = new SqliteMessageStore(_db);
    }

    public Task InitializeAsync() =>
        new DatabaseInitializer($"Data Source={_dbPath}", Logger).InitializeAsync();

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

    private static IncomingMessage Inbound(string text, bool blocked = false) => new()
    {
        Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "10001" },
        Sender = new SenderInfo { UserId = "10001", Nickname = "拉链" },
        Segments = [new TextSegment(text)],
        EventId = Guid.NewGuid().ToString("N"),
        OccurredAt = DateTimeOffset.UtcNow,
        Blocked = blocked,
    };

    private async Task<JsonElement> QueryAsync(string sessionId)
    {
        var action = new GetMessagesAction(_store);
        var payload = JsonSerializer.SerializeToElement(new { sessionId });
        var result = await action.HandleAsync(payload, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task Blocked_message_is_not_marked_unanswered()
    {
        await _store.AppendAsync(Inbound("陌生人的消息", blocked: true));

        var row = Assert.Single((await QueryAsync("qq:p10001")).EnumerateArray());

        Assert.True(row.GetProperty("blocked").GetBoolean());
        Assert.False(row.GetProperty("unanswered").GetBoolean());
    }

    [Fact]
    public async Task Allowed_message_without_reply_is_marked_unanswered()
    {
        await _store.AppendAsync(Inbound("在吗"));

        var row = Assert.Single((await QueryAsync("qq:p10001")).EnumerateArray());

        Assert.False(row.GetProperty("blocked").GetBoolean());
        Assert.True(row.GetProperty("unanswered").GetBoolean());
    }

    [Fact]
    public async Task Reply_clears_unanswered()
    {
        await _store.AppendAsync(Inbound("在吗"));
        await _store.AppendOutgoingAsync(
            new OutgoingMessage
            {
                Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "10001" },
                Segments = [new TextSegment("在的")],
            },
            "99999",
            DateTimeOffset.UtcNow.AddSeconds(1));

        var rows = (await QueryAsync("qq:p10001")).EnumerateArray().ToList();

        Assert.All(rows, r => Assert.False(r.GetProperty("unanswered").GetBoolean()));
    }
}
