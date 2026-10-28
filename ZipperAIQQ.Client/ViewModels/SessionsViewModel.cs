using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 会话页的视图模型：会话列表 / 消息 / 搜索 / 区间导出。
/// </summary>
/// <remarks>
/// 从 原 MainViewModel 抽出来（#35 的第一刀）：这块只被会话页使用。
/// 心跳由 Shell（ShellViewModel）按页分频调用 <see cref="RefreshIncrementalAsync"/>。
/// </remarks>
public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly ServerApiClient _api;
    private readonly AppSettings _settings;
    private string? _lastSessionId;

    public SessionsViewModel(ServerApiClient api, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(settings);

        _api = api;
        _settings = settings;
    }

    /// <summary>会话列表（服务端已按最后消息时间倒序）。</summary>
    public ObservableCollection<SessionDto> Sessions { get; } = [];

    /// <summary>当前会话的消息（按时间正序）。</summary>
    public ObservableCollection<MessageDto> Messages { get; } = [];

    [ObservableProperty]
    public partial SessionDto? SelectedSession { get; set; }

    [ObservableProperty]
    public partial string SessionStatus { get; set; } = "点「刷新会话」获取列表。";

    /// <summary>搜索词（匹配显示名或会话 ID）。</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>会话列表刷新完成（Shell 用它重建人设页的会话下拉）。</summary>
    public event Action? SessionsLoaded;

    partial void OnSelectedSessionChanged(SessionDto? value)
    {
        if (value is null)
        {
            return;
        }

        // 记住上次选的会话：重进客户端时还能读出来
        _settings.SelectedSessionId = value.SessionId;
        _settings.Save();

        _lastSessionId = null;   // 切会话 → 下一次心跳清空重来
        _ = LoadMessagesAsync();
    }

    [RelayCommand]
    private async Task LoadSessionsAsync()
    {
        try
        {
            object payload = string.IsNullOrWhiteSpace(SearchText)
                ? new { limit = 100 }
                : new { limit = 100, keyword = SearchText.Trim() };

            var list = await _api.CallAsync<List<SessionDto>>("listSessions", payload).ConfigureAwait(true);

            Sessions.Clear();
            foreach (var item in list ?? [])
            {
                Sessions.Add(item);
            }

            // 恢复上次选中的会话（搜索时若不在结果里就保持不选）
            if (!string.IsNullOrWhiteSpace(_settings.SelectedSessionId))
            {
                SelectedSession = Sessions.FirstOrDefault(s => s.SessionId == _settings.SelectedSessionId);
            }

            SessionStatus = string.IsNullOrWhiteSpace(SearchText)
                ? $"共 {Sessions.Count} 个会话"
                : $"「{SearchText.Trim()}」匹配 {Sessions.Count} 个会话";

            SessionsLoaded?.Invoke();
        }
        catch (Exception ex)
        {
            SessionStatus = $"刷新会话失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadMessagesAsync()
    {
        if (SelectedSession is null)
        {
            return;
        }

        try
        {
            var list = await _api
                .CallAsync<List<MessageDto>>("getMessages", new { sessionId = SelectedSession.SessionId, limit = 100 })
                .ConfigureAwait(true);

            Messages.Clear();
            foreach (var item in list ?? [])
            {
                Messages.Add(item);
            }

            _lastSessionId = SelectedSession.SessionId;
            SessionStatus = $"{SelectedSession.Title}：{Messages.Count} 条";
        }
        catch (Exception ex)
        {
            SessionStatus = $"加载消息失败：{ex.Message}";
        }
    }

    /// <summary>清空搜索并重新拉全量。</summary>
    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        SearchText = string.Empty;
        await LoadSessionsAsync().ConfigureAwait(true);
    }

    /// <summary>心跳用：只追加新消息（按 Id 去重），不整表重载。</summary>
    public async Task RefreshIncrementalAsync()
    {
        if (SelectedSession is null)
        {
            return;
        }

        if (_lastSessionId != SelectedSession.SessionId)
        {
            _lastSessionId = SelectedSession.SessionId;
            Messages.Clear();
        }

        var list = await _api
            .CallAsync<List<MessageDto>>("getMessages", new { sessionId = SelectedSession.SessionId, limit = 50 })
            .ConfigureAwait(true);

        if (list is null || list.Count == 0)
        {
            return;
        }

        var known = Messages.Select(m => m.Id).ToHashSet();
        var fresh = list.Where(m => !known.Contains(m.Id)).OrderBy(m => m.Id).ToList();

        foreach (var item in fresh)
        {
            Messages.Add(item);
        }

        if (fresh.Count > 0)
        {
            SessionStatus = $"{SelectedSession.Title}：{Messages.Count} 条（+{fresh.Count}）";
        }
    }

    /// <summary>日期选择器 → 导出起点（按本地时间当天 00:00）。</summary>
    public static DateTimeOffset ExportStart(DateTimeOffset date) =>
        new DateTimeOffset(date.Date, TimeSpan.Zero).ToLocalTime();

    /// <summary>日期选择器 → 导出终点（按本地时间当天 23:59:59.999）。</summary>
    public static DateTimeOffset ExportEnd(DateTimeOffset date) =>
        new DateTimeOffset(date.Date.AddDays(1).AddMilliseconds(-1), TimeSpan.Zero).ToLocalTime();

    /// <summary>
    /// 按可选时间区间导出（#37）。区间为空则导出当前已加载的列表。
    /// </summary>
    /// <returns>导出文件路径。</returns>
    public async Task<string> ExportAsync(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (SelectedSession is null)
        {
            throw new InvalidOperationException("先在左边选一个会话。");
        }

        IReadOnlyList<MessageDto> rows;
        string rangeLabel;

        if (from is null && to is null)
        {
            if (Messages.Count == 0)
            {
                throw new InvalidOperationException("先加载消息再导出。");
            }

            rows = Messages.ToList();
            rangeLabel = "已加载";
        }
        else
        {
            var payload = new
            {
                sessionId = SelectedSession.SessionId,
                limit = 5000,
                from = from?.ToUnixTimeMilliseconds(),
                to = to?.ToUnixTimeMilliseconds(),
            };

            rows = await _api.CallAsync<List<MessageDto>>("getMessages", payload).ConfigureAwait(true) ?? [];
            if (rows.Count == 0)
            {
                throw new InvalidOperationException("这个区间里没有消息。");
            }

            rangeLabel = $"{from?.LocalDateTime:yyyyMMdd}-{to?.LocalDateTime:yyyyMMdd}";
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZipperAIQQ", "exports");
        Directory.CreateDirectory(dir);

        var safe = new string(SelectedSession.SessionId
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray());
        var file = Path.Combine(dir, $"session-{safe}-{rangeLabel}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var content = string.Join(Environment.NewLine, rows.Select(m => m.Display));
        await File.WriteAllTextAsync(file, content, new UTF8Encoding(false)).ConfigureAwait(true);

        SessionStatus = $"已导出 {rows.Count} 条：{file}";
        return file;
    }
}

