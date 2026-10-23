using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>服务端日志页（#31）：时间 / 级别 / 关键词检索。</summary>
public sealed partial class LogsViewModel : ObservableObject
{
    private readonly ServerApiClient _api;

    public LogsViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public ObservableCollection<LogLineDto> Lines { get; } = [];

    public string[] Levels { get; } = ["", "INF", "WRN", "ERR"];

    [ObservableProperty]
    public partial string SelectedLevel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Keyword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? From { get; set; } = DateTimeOffset.Now.Date.AddDays(-1);

    [ObservableProperty]
    public partial DateTimeOffset? To { get; set; } = DateTimeOffset.Now;

    [ObservableProperty]
    public partial string Status { get; set; } = "选好条件后点「查询」。";

    [RelayCommand]
    private async Task QueryAsync()
    {
        try
        {
            object payload = new
            {
                from = From?.ToUnixTimeMilliseconds(),
                to = To?.ToUnixTimeMilliseconds(),
                level = string.IsNullOrWhiteSpace(SelectedLevel) ? null : SelectedLevel,
                keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim(),
                limit = 300,
            };

            var rows = await _api.CallAsync<List<LogLineDto>>("queryLogs", payload).ConfigureAwait(true) ?? [];

            Lines.Clear();
            foreach (var row in rows)
            {
                Lines.Add(row);
            }

            Status = $"共 {Lines.Count} 条（最新在前）";
        }
        catch (Exception ex)
        {
            Status = $"查询失败：{ex.Message}";
        }
    }
}
