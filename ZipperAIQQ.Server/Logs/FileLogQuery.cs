using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;

namespace ZipperAIQQ.Server.Logs;

/// <summary>一条服务端运行日志。</summary>
public sealed record LogLine(DateTimeOffset At, string Level, string Message, string File);

/// <summary>日志查询条件（#31）。</summary>
public sealed record LogQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Level,
    string? Keyword,
    int Limit,
    int Offset);

/// <summary>读 <c>logs/zipper-*.log</c> 的只读查询。</summary>
public interface ILogQuery
{
    Task<IReadOnlyList<LogLine>> QueryAsync(LogQuery query, CancellationToken ct = default);
}

/// <summary>
/// 纯文本日志检索（Serilog 文件 sink 的固定格式）。
/// </summary>
/// <remarks>
/// <para>
/// 不做索引：单文件上限 32MB、保留 14 个，按文件名日期先裁掉无关文件，
/// 再逐行匹配，足够快且不引入新存储。
/// </para>
/// <para>
/// 只返回**首行**（异常堆栈的后续行不逐条返回）：一次查 500 条时把堆栈也带上，
/// 响应会大得没法用；需要堆栈时按时间点去文件里看原文。
/// </para>
/// </remarks>
public sealed partial class FileLogQuery : ILogQuery
{
    private readonly string _directory;
    private readonly ILogger _logger;

    public FileLogQuery(string directory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(logger);

        _directory = directory;
        _logger = logger;
    }

    [GeneratedRegex(@"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (?<level>[A-Z]{3})\] (?<msg>.*)$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"zipper-(?<date>\d{8})")]
    private static partial Regex FileDateRegex();

    public Task<IReadOnlyList<LogLine>> QueryAsync(LogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Task.Run<IReadOnlyList<LogLine>>(() =>
        {
            if (!Directory.Exists(_directory))
            {
                return [];
            }

            var limit = Math.Clamp(query.Limit, 1, 500);
            var offset = Math.Max(0, query.Offset);
            var results = new List<LogLine>();

            // 文件名带日期：先按区间裁文件，避免扫全部
            var files = Directory
                .EnumerateFiles(_directory, "zipper-*.log")
                .Select(path => (Path: path, Date: ParseFileDate(path)))
                .Where(x => x.Date is not null)
                .Where(x => query.From is null || x.Date >= query.From.Value.Date)
                .Where(x => query.To is null || x.Date <= query.To.Value.Date)
                .OrderByDescending(x => x.Date)
                .Select(x => x.Path)
                .ToList();

            // 新的在前：从最后一天往前读，凑够 limit+offset 就停
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var lines = File.ReadLines(file);
                var buffer = new List<LogLine>();

                foreach (var line in lines)
                {
                    var match = LineRegex().Match(line);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var at = DateTimeOffset.ParseExact(
                        match.Groups["time"].Value,
                        "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal);

                    if (query.From is { } from && at < from)
                    {
                        continue;
                    }

                    if (query.To is { } to && at > to)
                    {
                        continue;
                    }

                    var level = match.Groups["level"].Value;
                    if (!string.IsNullOrWhiteSpace(query.Level) &&
                        !string.Equals(level, query.Level, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var message = match.Groups["msg"].Value;
                    if (!string.IsNullOrWhiteSpace(query.Keyword) &&
                        !message.Contains(query.Keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    buffer.Add(new LogLine(at, level, message, Path.GetFileName(file)));
                }

                // 文件内正序，收集后整体倒序
                for (var i = buffer.Count - 1; i >= 0; i--)
                {
                    if (results.Count < offset + limit)
                    {
                        results.Add(buffer[i]);
                    }
                }

                if (results.Count >= offset + limit)
                {
                    break;
                }
            }

            return results.Skip(offset).Take(limit).ToList();
        }, ct);
    }

    private static DateTimeOffset? ParseFileDate(string path)
    {
        var match = FileDateRegex().Match(Path.GetFileName(path));
        return match.Success &&
               DateTime.TryParseExact(
                   match.Groups["date"].Value,
                   "yyyyMMdd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var date)
            ? new DateTimeOffset(date, TimeSpan.FromHours(8))
            : null;
    }
}
