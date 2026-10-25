using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>外部接口（WS）配置页（问题记录 03 #5）。</summary>
public sealed partial class ExternalViewModel : ObservableObject
{
    private readonly ServerApiClient _api;

    public ExternalViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    [ObservableProperty]
    public partial bool AllowSend { get; set; }

    [ObservableProperty]
    public partial string AllowedSessions { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ForwardUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ForwardToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var external = await _api.CallAsync<ExternalDto>("getExternal").ConfigureAwait(true);
            AllowSend = external?.AllowSend == true;
            AllowedSessions = external?.AllowedSessions is { Length: > 0 }
                ? string.Join(", ", external.AllowedSessions)
                : string.Empty;
            ForwardUrl = external?.ForwardUrl ?? string.Empty;
            ForwardToken = string.Empty;
            Status = external?.HasForwardToken == true ? "已配置 token" : "未配置 token";
        }
        catch (Exception ex)
        {
            Status = $"加载失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var sessions = AllowedSessions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            await _api.CallAsync<object>("setExternal", new
            {
                allowSend = AllowSend,
                allowedSessions = sessions,
                forwardUrl = ForwardUrl.Trim(),
                forwardToken = string.IsNullOrWhiteSpace(ForwardToken) ? null : ForwardToken.Trim(),
            }).ConfigureAwait(true);

            ForwardToken = string.Empty;
            Status = "已保存（正向连接会立即重连）";
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }
}
