using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 端点与参数页：模型端点 / 运行参数 / 行为设置 / JEV 判断模型。
/// </summary>
/// <remarks>
/// 2026-10-23 从 <see cref="MainViewModel"/> 拆出（#8 #9 #11 #12 #13 #18 #27）：
/// 只服务于这一页，状态自己管，保存结果就地显示。
/// </remarks>
public sealed partial class EndpointsViewModel : ObservableObject
{
    private readonly ServerApiClient _api;

    public EndpointsViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public ObservableCollection<string> EndpointNames { get; } = [];

    public ObservableCollection<SettingItem> Settings { get; } = [];

    public ObservableCollection<string> ReplyModes { get; } = [];

    public ObservableCollection<NoticeToggleItem> NoticeToggles { get; } = [];

    [ObservableProperty]
    public partial string SelectedEndpointName { get; set; } = "primary";

    [ObservableProperty]
    public partial string EndpointBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EndpointModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EndpointKey { get; set; } = string.Empty;

    /// <summary>端点名是固定的（#12）：界面只读展示。</summary>
    public string EndpointNameReadOnly => SelectedEndpointName;

    [ObservableProperty]
    public partial string EndpointStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SettingsStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BehaviorStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReplyMode { get; set; } = "every";

    [ObservableProperty]
    public partial string JudgeBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JudgeModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string JudgeKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double JudgeThreshold { get; set; } = 0.55;

    [ObservableProperty]
    public partial string JudgeStatus { get; set; } = string.Empty;

    private ConfigDto? _config;

    partial void OnSelectedEndpointNameChanged(string value)
    {
        EndpointNameReadOnlyOnChange();
        ApplyEndpoint(value);
    }

    private void EndpointNameReadOnlyOnChange() => OnPropertyChanged(nameof(EndpointNameReadOnly));

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            _config = await _api.CallAsync<ConfigDto>("getConfig").ConfigureAwait(true);

            EndpointNames.Clear();
            var names = _config?.EndpointNames is { Count: > 0 }
                ? _config.EndpointNames
                : ["primary", "judge"];

            foreach (var name in names)
            {
                EndpointNames.Add(name);
            }

            // #11：按角色映射选默认端点，不再取 Endpoints[0]（那恒为字母序最前的 judge）
            var chat = _config?.Roles.TryGetValue("Chat", out var chatName) == true ? chatName : "primary";
            SelectedEndpointName = EndpointNames.Contains(chat) ? chat : EndpointNames.FirstOrDefault() ?? "primary";

            ApplyEndpoint(SelectedEndpointName);
            ApplyJudge();

            await LoadSettingsAsync().ConfigureAwait(true);
            await LoadBehaviorAsync().ConfigureAwait(true);

            EndpointStatus = "已加载";
        }
        catch (Exception ex)
        {
            EndpointStatus = $"加载失败：{ex.Message}";
        }
    }

    private void ApplyEndpoint(string name)
    {
        var endpoint = _config?.Endpoints.FirstOrDefault(e =>
            string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        EndpointBaseUrl = endpoint?.BaseUrl ?? string.Empty;
        EndpointModel = endpoint?.Model ?? string.Empty;
        EndpointKey = string.Empty;
        EndpointStatus = string.Empty;
    }

    [RelayCommand]
    private async Task SaveEndpointAsync()
    {
        try
        {
            var result = await _api.CallAsync<SaveResultDto>("setConfig", new
            {
                endpoint = new
                {
                    name = SelectedEndpointName,
                    baseUrl = EndpointBaseUrl.Trim(),
                    model = EndpointModel.Trim(),
                    key = string.IsNullOrWhiteSpace(EndpointKey) ? null : EndpointKey,
                },
            }).ConfigureAwait(true);

            EndpointStatus = result?.Skipped == true ? "无变化" : "端点已保存";
            EndpointKey = string.Empty;
            _config = await _api.CallAsync<ConfigDto>("getConfig").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            EndpointStatus = $"保存失败：{ex.Message}";
        }
    }

    private async Task LoadSettingsAsync()
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

        JudgeThreshold = Settings.FirstOrDefault(s => s.Key == "judge.threshold")?.Value ?? 0.55;
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
            await LoadSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SettingsStatus = $"保存失败：{ex.Message}";
        }
    }

    private async Task LoadBehaviorAsync()
    {
        var data = await _api.CallAsync<BehaviorDto>("getBehavior").ConfigureAwait(true);
        if (data is null)
        {
            return;
        }

        ReplyModes.Clear();
        foreach (var mode in data.ReplyModes)
        {
            ReplyModes.Add(mode);
        }

        ReplyMode = data.ReplyMode;

        NoticeToggles.Clear();
        foreach (var item in data.Notices)
        {
            NoticeToggles.Add(new NoticeToggleItem
            {
                Type = item.Type,
                Label = item.Label,
                Enabled = item.Enabled,
            });
        }
    }

    [RelayCommand]
    private async Task SaveBehaviorAsync()
    {
        try
        {
            var notice = NoticeToggles.ToDictionary(n => n.Type, n => n.Enabled);
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

    private void ApplyJudge()
    {
        var judge = _config?.Endpoints.FirstOrDefault(e =>
            string.Equals(e.Name, "judge", StringComparison.OrdinalIgnoreCase));

        JudgeBaseUrl = judge?.BaseUrl ?? string.Empty;
        JudgeModel = judge?.Model ?? string.Empty;
        JudgeKey = string.Empty;
    }

    [RelayCommand]
    private async Task SaveJudgeAsync()
    {
        try
        {
            var result = await _api.CallAsync<SaveResultDto>("setConfig", new
            {
                endpoint = new
                {
                    name = "judge",
                    baseUrl = JudgeBaseUrl.Trim(),
                    model = JudgeModel.Trim(),
                    key = string.IsNullOrWhiteSpace(JudgeKey) ? null : JudgeKey,
                },
            }).ConfigureAwait(true);

            JudgeStatus = result?.Skipped == true ? "无变化" : "JEV 端点已保存";
            JudgeKey = string.Empty;
            _config = await _api.CallAsync<ConfigDto>("getConfig").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            JudgeStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveThresholdAsync()
    {
        try
        {
            var result = await _api.CallAsync<SaveResultDto>("setSettings", new
            {
                settings = new Dictionary<string, double> { ["judge.threshold"] = JudgeThreshold },
            }).ConfigureAwait(true);

            JudgeStatus = result?.Skipped == true ? "无变化" : $"判断灵敏度已保存（{JudgeThreshold:F2}）";
            await LoadSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            JudgeStatus = $"保存失败：{ex.Message}";
        }
    }
}
