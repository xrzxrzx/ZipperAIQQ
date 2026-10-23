using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 运行与行为页：数值型运行参数 + 回复模式 / 通知响应。
/// </summary>
/// <remarks>
/// 从「端点与参数」里拆出来（用户 2026-10-23 反馈：AI 端点配置应独立一页）。
/// </remarks>
public sealed partial class RuntimeViewModel : ObservableObject
{
    private readonly ServerApiClient _api;
    private readonly List<NoticeToggleItem> _allNotices = [];

    public RuntimeViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public ObservableCollection<SettingItem> Settings { get; } = [];

    public ObservableCollection<string> ReplyModes { get; } = [];

    public ObservableCollection<NoticeToggleItem> CommonNotices { get; } = [];

    public ObservableCollection<NoticeToggleItem> OtherNotices { get; } = [];

    [ObservableProperty]
    public partial string ReplyMode { get; set; } = "every";

    [ObservableProperty]
    public partial string NoticeSearch { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SettingsStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BehaviorStatus { get; set; } = string.Empty;

    partial void OnNoticeSearchChanged(string value) => ApplyNoticeFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var items = await _api.CallAsync<List<SettingDto>>("getSettings").ConfigureAwait(true) ?? [];

            Settings.Clear();
            foreach (var item in items)
            {
                Settings.Add(new SettingItem
                {
                    Key = item.Key,
                    Label = item.Label,
                    Min = item.Min,
                    Max = item.Max,
                    Unit = item.Unit,
                    Stored = item.Stored,
                    Value = item.Value,
                });
            }

            var behavior = await _api.CallAsync<BehaviorDto>("getBehavior").ConfigureAwait(true);
            if (behavior is not null)
            {
                ReplyModes.Clear();
                foreach (var mode in behavior.ReplyModes)
                {
                    ReplyModes.Add(mode);
                }

                ReplyMode = behavior.ReplyMode;

                _allNotices.Clear();
                foreach (var item in behavior.Notices)
                {
                    _allNotices.Add(new NoticeToggleItem
                    {
                        Type = item.Type,
                        Label = item.Label,
                        Group = item.Group,
                        Enabled = item.Enabled,
                    });
                }

                ApplyNoticeFilter();
            }

            SettingsStatus = "已加载";
        }
        catch (Exception ex)
        {
            SettingsStatus = $"加载失败：{ex.Message}";
        }
    }

    private void ApplyNoticeFilter()
    {
        var keyword = NoticeSearch.Trim();

        bool Match(NoticeToggleItem item) =>
            string.IsNullOrEmpty(keyword) ||
            item.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            item.Type.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        CommonNotices.Clear();
        OtherNotices.Clear();

        foreach (var item in _allNotices.Where(Match))
        {
            if (item.Group == "common")
            {
                CommonNotices.Add(item);
            }
            else
            {
                OtherNotices.Add(item);
            }
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var outOfRange = Settings.FirstOrDefault(s => s.IsOutOfRange);
            if (outOfRange is not null)
            {
                SettingsStatus = $"「{outOfRange.Label}」超出范围（{outOfRange.Hint}）";
                return;
            }

            var payload = Settings.ToDictionary(s => s.Key, s => s.Value);
            var result = await _api.CallAsync<SaveResultDto>("setSettings", new { settings = payload })
                .ConfigureAwait(true);

            SettingsStatus = result?.Skipped == true ? "无变化" : $"已保存 {result?.Updated ?? 0} 项";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SettingsStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveBehaviorAsync()
    {
        try
        {
            var notice = _allNotices.ToDictionary(n => n.Type, n => n.Enabled);
            var result = await _api.CallAsync<SaveResultDto>("setBehavior", new
            {
                replyMode = ReplyMode,
                notice,
            }).ConfigureAwait(true);

            BehaviorStatus = result?.Skipped == true ? "无变化" : "行为设置已保存";
        }
        catch (Exception ex)
        {
            BehaviorStatus = $"保存失败：{ex.Message}";
        }
    }
}
