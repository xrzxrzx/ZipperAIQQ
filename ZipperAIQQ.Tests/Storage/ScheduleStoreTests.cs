using Serilog;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Schedules;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>
/// 定时任务仓储：创建幂等的时间窗查询（问题记录 04 #6）。
/// </summary>
/// <remarks>
/// 用临时文件库（与 <see cref="MetricsStoreTests"/> 同口径）：
/// <see cref="SqliteDatabase"/> 每次读写开新连接，<c>:memory:</c> 会跟着连接消失。
/// </remarks>
public sealed class ScheduleStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 11, 8, 8, 0, 0, TimeSpan.FromHours(8));

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteScheduleStore _store;

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    public ScheduleStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-schedule-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _store = new SqliteScheduleStore(_db);
    }

    public async Task InitializeAsync() =>
        await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();

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

    private Task<long> AddAsync(
        DateTimeOffset at,
        string sessionId = "qq:p10001",
        ScheduleRepeat repeat = ScheduleRepeat.Once,
        int intervalMinutes = 0,
        string content = "提醒") =>
        _store.CreateAsync(new ScheduledTask(
            Id: 0,
            Chat: ChatOf(sessionId),
            Content: content,
            MentionUserId: null,
            Repeat: repeat,
            IntervalMinutes: intervalMinutes,
            NextRunAt: at,
            CreatedAt: Base.AddDays(-1),
            CreatedBy: "10001",
            Status: ScheduleStatus.Pending));

    private static ChatRef ChatOf(string sessionId)
    {
        // qq:p10001 → 私聊 10001
        var id = sessionId[(sessionId.IndexOf(':') + 2)..];
        return new ChatRef { Platform = "qq", Type = ChatType.Private, Id = id };
    }

    // ══════ 用例 ══════

    [Fact]
    public async Task Same_time_within_tolerance_is_found_even_if_content_differs()
    {
        // 实测形态：#10 与 #11 同到期、文案不同
        await AddAsync(Base, content: "昨天说的那个事，该修了");
        await AddAsync(Base.AddMinutes(1), content: "先查内核版本");

        var found = await _store.FindSimilarAsync(
            "qq:p10001", Base.AddMinutes(1), ScheduleRepeat.Once, 0, TimeSpan.FromMinutes(2));

        Assert.NotNull(found);
    }

    [Fact]
    public async Task Time_outside_tolerance_is_not_a_duplicate()
    {
        await AddAsync(Base);

        var found = await _store.FindSimilarAsync(
            "qq:p10001", Base.AddMinutes(5), ScheduleRepeat.Once, 0, TimeSpan.FromMinutes(2));

        Assert.Null(found);
    }

    [Fact]
    public async Task Different_repeat_shape_is_not_a_duplicate()
    {
        await AddAsync(Base, repeat: ScheduleRepeat.Interval, intervalMinutes: 1440);

        var once = await _store.FindSimilarAsync(
            "qq:p10001", Base, ScheduleRepeat.Once, 0, TimeSpan.FromMinutes(2));
        var otherInterval = await _store.FindSimilarAsync(
            "qq:p10001", Base, ScheduleRepeat.Interval, 60, TimeSpan.FromMinutes(2));

        Assert.Null(once);
        Assert.Null(otherInterval);
    }

    [Fact]
    public async Task Cancelled_task_is_ignored()
    {
        var id = await AddAsync(Base);
        Assert.True(await _store.CancelAsync(id, "qq:p10001"));

        var found = await _store.FindSimilarAsync(
            "qq:p10001", Base, ScheduleRepeat.Once, 0, TimeSpan.FromMinutes(2));

        Assert.Null(found);
    }

    [Fact]
    public async Task Failed_task_leaves_the_due_queue()
    {
        var id = await AddAsync(Base.AddMinutes(-5));

        await _store.MarkFailedAsync(id);

        Assert.Empty(await _store.GetDueAsync(Base, 10));
        Assert.Equal(0, await _store.CountPendingAsync());

        var listed = await _store.ListAsync("qq:p10001");
        Assert.Equal(ScheduleStatus.Failed, Assert.Single(listed).Status);
    }

    [Fact]
    public async Task Other_session_is_ignored()
    {
        await AddAsync(Base, sessionId: "qq:p20002");

        var found = await _store.FindSimilarAsync(
            "qq:p10001", Base, ScheduleRepeat.Once, 0, TimeSpan.FromMinutes(2));

        Assert.Null(found);
    }
}
