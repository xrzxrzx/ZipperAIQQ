using Serilog;
using ZipperAIQQ.Server.Logs;

namespace ZipperAIQQ.Tests.Server;

/// <summary>日志检索（#31）：文件格式、过滤、分页、与 Serilog 同时写盘。</summary>
public sealed class LogQueryTests : IDisposable
{
    private readonly string _dir;
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    public LogQueryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zipper-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    private void WriteLog(string date, params string[] lines)
    {
        File.WriteAllLines(
            Path.Combine(_dir, $"zipper-{date}.log"),
            lines,
            new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public async Task Query_FiltersByKeywordAndLevel_AndReturnsNewestFirst()
    {
        WriteLog(
            "20261025",
            "[2026-10-25 10:00:00.000 INF] 普通消息",
            "[2026-10-25 10:01:00.000 WRN] 警告：猫粮没了",
            "[2026-10-25 10:02:00.000 ERR] 炸了");

        var query = new FileLogQuery(_dir, _logger);

        var all = await query.QueryAsync(new LogQuery(null, null, null, null, 10, 0));
        Assert.Equal(3, all.Count);
        Assert.Equal("ERR", all[0].Level);
        Assert.Equal("WRN", all[1].Level);
        Assert.Equal("INF", all[2].Level);

        var warn = await query.QueryAsync(new LogQuery(null, null, "WRN", null, 10, 0));
        Assert.Single(warn);
        Assert.Contains("猫粮", warn[0].Message);

        var keyword = await query.QueryAsync(new LogQuery(null, null, null, "猫粮", 10, 0));
        Assert.Single(keyword);
    }

    [Fact]
    public async Task Query_RespectsTimeRangeAndPaging()
    {
        WriteLog(
            "20261025",
            "[2026-10-25 08:00:00.000 INF] 一",
            "[2026-10-25 09:00:00.000 INF] 二",
            "[2026-10-25 10:00:00.000 INF] 三");

        var query = new FileLogQuery(_dir, _logger);
        var from = new DateTimeOffset(2026, 10, 25, 8, 30, 0, TimeSpan.FromHours(8));
        var to = new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.FromHours(8));

        var range = await query.QueryAsync(new LogQuery(from, to, null, null, 10, 0));
        Assert.Single(range);
        Assert.Equal("二", range[0].Message);

        var page = await query.QueryAsync(new LogQuery(null, null, null, null, 1, 1));
        Assert.Single(page);
        Assert.Equal("二", page[0].Message);
    }

    [Fact]
    public async Task Query_SkipsFilesOutsideRange()
    {
        WriteLog("20260101", "[2026-01-01 00:00:00.000 INF] 旧日志");
        WriteLog("20261025", "[2026-10-25 00:00:00.000 INF] 新日志");

        var query = new FileLogQuery(_dir, _logger);
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(8));

        var rows = await query.QueryAsync(new LogQuery(from, null, null, null, 10, 0));
        Assert.Single(rows);
        Assert.Equal("新日志", rows[0].Message);
    }

    [Fact]
    public async Task Query_WorksWhileFileIsOpenForWriting()
    {
        // 模拟 Serilog 正持有文件写句柄：FileShare.Read 不允许别的写者，
        // 我们的读取必须用 ReadWrite 共享模式才不会 IOException（线上 500 的根因）。
        WriteLog("20261025", "[2026-10-25 10:00:00.000 INF] 正在写");

        var path = Path.Combine(_dir, "zipper-20261025.log");
        await using var writer = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);

        var query = new FileLogQuery(_dir, _logger);
        var rows = await query.QueryAsync(new LogQuery(null, null, null, null, 10, 0));

        Assert.Single(rows);
        Assert.Equal("正在写", rows[0].Message);
    }

    [Fact]
    public async Task Query_MissingDirectory_ReturnsEmpty()
    {
        var query = new FileLogQuery(Path.Combine(_dir, "nope"), _logger);
        var rows = await query.QueryAsync(new LogQuery(null, null, null, null, 10, 0));
        Assert.Empty(rows);
    }
}
