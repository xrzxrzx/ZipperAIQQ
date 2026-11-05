using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Cost;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Schedules;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// <c>schedule_create</c> 的创建幂等（问题记录 04 #6）。
/// </summary>
/// <remarks>
/// 走真的 <see cref="SqliteScheduleStore"/>（临时文件库）+ 真的 <see cref="AIFunction"/>，
/// 断言的是「模型连调两次时库里到底几条」—— 这正是用户观察到的现象。
/// </remarks>
public sealed class ScheduleToolsTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;
    private readonly SqliteScheduleStore _store;
    private readonly AIFunction _create;

    public ScheduleToolsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-schedule-tool-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
        _store = new SqliteScheduleStore(_db);

        var context = new ToolCallContext
        {
            Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "10001" },
            Sender = new SenderInfo { UserId = "10001" },
            Role = Role.Admin,
        };

        _create = ScheduleTools
            .Create(context, new AllowAllGate(), _store)
            .OfType<AIFunction>()
            .Single(t => t.Name == "schedule_create");
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

    private sealed class AllowAllGate : IPermissionGate
    {
        public PermissionDecision Check(ToolCapability capability, ToolCallContext context) =>
            PermissionDecision.Allow();

        public bool IsAllowed(ToolCapability capability, Role role) => true;
    }

    private async Task<string> CreateAsync(string when, string content, bool? allowDuplicate = null)
    {
        // AIFunctionFactory 要求**每个参数都出现在字典里**（可空参数也不例外），
        // 缺键会直接抛。这里把不用到的参数显式写 null。
        var args = new AIFunctionArguments
        {
            ["when"] = when,
            ["content"] = content,
            ["mentionUser"] = null,
            ["repeatMinutes"] = null,
            ["target"] = null,
            ["allowDuplicate"] = allowDuplicate,
        };

        var result = await _create.InvokeAsync(args, CancellationToken.None).ConfigureAwait(false);
        return result?.ToString() ?? string.Empty;
    }

    /// <summary>工具入参格式是 <c>yyyy-MM-dd HH:mm</c>；<see cref="BeijingTime.Format"/> 带星期，不能直接用。</summary>
    private static string When(DateTimeOffset local) =>
        BeijingTime.ToBeijing(local).ToString("yyyy-MM-dd HH:mm");

    // ══════ 用例 ══════

    [Fact]
    public async Task Second_create_at_same_minute_is_rejected_as_duplicate()
    {
        var at = BeijingTime.Now.AddHours(3);

        var first = await CreateAsync(When(at), "该去修那个漏洞了");
        var second = await CreateAsync(When(at), "先查内核版本，再看补丁");

        Assert.Contains("已创建定时任务 #", first);
        Assert.Contains("已存在同一时刻附近的定时任务 #", second);
        Assert.Contains("未重复创建", second);
        Assert.Equal(1, await _store.CountPendingAsync());
    }

    [Fact]
    public async Task Second_create_within_two_minutes_is_also_rejected()
    {
        // 实测形态：#7 / #8 只差 1 分钟
        var at = BeijingTime.Now.AddHours(4);

        await CreateAsync(When(at), "第一条");
        var second = await CreateAsync(When(at.AddMinutes(1)), "第二条");

        Assert.Contains("已存在", second);
        Assert.Equal(1, await _store.CountPendingAsync());
    }

    [Fact]
    public async Task AllowDuplicate_escape_hatch_creates_the_second_task()
    {
        var at = BeijingTime.Now.AddHours(5);

        await CreateAsync(When(at), "八点吃药");
        var second = await CreateAsync(When(at), "八点开会", allowDuplicate: true);

        Assert.Contains("已创建定时任务 #", second);
        Assert.Equal(2, await _store.CountPendingAsync());
    }

    [Fact]
    public async Task Creating_while_executing_a_task_is_blocked()
    {
        // G-4：执行中的定时任务不能再创建新任务（防无限套娃）
        using var scope = ScheduleExecutionScope.Begin();

        var result = await CreateAsync(When(BeijingTime.Now.AddHours(7)), "套娃提醒");

        Assert.Contains("正在执行定时任务", result);
        Assert.Equal(0, await _store.CountPendingAsync());
    }

    [Fact]
    public async Task Different_time_is_not_a_duplicate()
    {
        var at = BeijingTime.Now.AddHours(6);

        await CreateAsync(When(at), "第一条");
        var second = await CreateAsync(When(at.AddMinutes(30)), "第二条");

        Assert.Contains("已创建定时任务 #", second);
        Assert.Equal(2, await _store.CountPendingAsync());
    }
}
