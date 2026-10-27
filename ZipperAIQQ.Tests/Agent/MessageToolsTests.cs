using Serilog;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// 消息查询跨平台（S3-G / 决策十）：结果带来源、按平台收窄。
/// </summary>
/// <remarks>
/// 两条主张各测一半：**存储层**负责"按平台筛"，**渲染层**负责"每条带来源"。
/// 只测其中一个都会漏 —— 筛对了但没标来源，模型照样分不清；标了来源但没筛，回答会串平台。
/// </remarks>
public sealed class MessageToolsTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-g-{Guid.NewGuid():N}.db");
    private string _connectionString = string.Empty;
    private SqliteDatabase _db = null!;
    private SqliteMessageStore _store = null!;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source={_dbPath}";
        await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();

        _db = new SqliteDatabase(_connectionString, Logger);
        _store = new SqliteMessageStore(_db);

        await _store.AppendAsync(Message("qq", "qq:p1", "10001", "复活节那天我在"));
        await _store.AppendAsync(Message("qq", "qq:g2", "20002", "复活节的事你问过了"));
        await _store.AppendAsync(Message("douyin", "douyin:g3", "dy-9", "复活节直播记得看"));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 连接池可能还占着句柄
        }
    }

    [Fact]
    public async Task SearchAcrossSessions_WithoutPlatform_SearchesAllPlatforms()
    {
        var hits = await _store.SearchAcrossSessionsAsync("复活节", 10);

        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, h => h.Message.Chat.Platform == "qq");
        Assert.Contains(hits, h => h.Message.Chat.Platform == "douyin");
    }

    [Fact]
    public async Task SearchAcrossSessions_WithPlatform_ReturnsOnlyThatPlatform()
    {
        var hits = await _store.SearchAcrossSessionsAsync("复活节", 10, platform: "douyin");

        Assert.Single(hits);
        Assert.Equal("douyin", hits[0].Message.Chat.Platform);
        Assert.Equal("douyin:g3", hits[0].Message.Chat.SessionId);
    }

    [Fact]
    public async Task SearchAcrossSessions_ExcludesCurrentSession_EvenWithPlatformFilter()
    {
        var hits = await _store.SearchAcrossSessionsAsync(
            "复活节", 10, excludeSessionId: "qq:g2", platform: "qq");

        Assert.Single(hits);
        Assert.Equal("qq:p1", hits[0].Message.Chat.SessionId);
    }

    [Fact]
    public async Task FormatForModel_EveryLineCarriesPlatformSessionSource()
    {
        var rows = await _store.SearchAcrossSessionsAsync("复活节", 10);

        var text = MessageTools.FormatForModel(rows);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(rows.Count, lines.Length);
        foreach (var line in lines)
        {
            // 形如「[qq:p1] [10-27 14:00:00 周一] 测试：…」
            Assert.StartsWith("[", line, StringComparison.Ordinal);
            var close = line.IndexOf(']');
            Assert.True(close > 1, $"缺少来源标签：{line}");

            var source = line[1..close];
            Assert.Contains(":", source, StringComparison.Ordinal);
            Assert.True(
                source.StartsWith("qq:", StringComparison.Ordinal)
                || source.StartsWith("douyin:", StringComparison.Ordinal),
                $"来源标签不是 [平台:会话]：{source}");
        }
    }

    [Fact]
    public async Task FormatForModel_OutgoingMessageShowsMe()
    {
        await _store.AppendOutgoingAsync(
            new OutgoingMessage
            {
                Chat = new ChatRef { Platform = "douyin", Type = ChatType.Private, Id = "dy-9" },
                Segments = [new TextSegment("复活节见")],
            },
            "bot-dy",
            DateTimeOffset.UtcNow);

        var rows = await _store.SearchAcrossSessionsAsync("复活节见", 10);
        var text = MessageTools.FormatForModel(rows);

        Assert.Contains("我：复活节见", text, StringComparison.Ordinal);
        Assert.StartsWith("[douyin:pdy-9]", text, StringComparison.Ordinal);
    }

    private static IncomingMessage Message(string platform, string sessionId, string userId, string text)
    {
        var parts = sessionId.Split(':', 2);
        var chatId = parts[1][1..]; // 去掉 g / p 前缀
        var type = parts[1][0] == 'g' ? ChatType.Group : ChatType.Private;

        return new IncomingMessage
        {
            Chat = new ChatRef { Platform = platform, Type = type, Id = chatId },
            Sender = new SenderInfo { UserId = userId, Nickname = "测试" },
            Segments = [new TextSegment(text)],
            OccurredAt = DateTimeOffset.UtcNow,
            EventId = Guid.NewGuid().ToString("N"),
        };
    }
}
