using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>一条反向 WS 监听配置（可在页面里增删）。</summary>
public sealed partial class ReverseServerItem : ObservableObject
{
    [ObservableProperty]
    public partial string Host { get; set; } = "0.0.0.0";

    [ObservableProperty]
    public partial double Port { get; set; } = 5181;

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;
}

/// <summary>外部接口（WS）配置页（问题记录 03 #5）。</summary>
public sealed partial class ExternalViewModel : ObservableObject
{
    private readonly ServerApiClient _api;

    public ExternalViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public ObservableCollection<ReverseServerItem> ReverseServers { get; } = [];

    public string[] Hosts { get; } = ["0.0.0.0", "127.0.0.1"];

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

            ReverseServers.Clear();
            foreach (var item in external?.ReverseServers ?? [])
            {
                ReverseServers.Add(new ReverseServerItem
                {
                    Host = item.Host,
                    Port = item.Port,
                    Token = string.Empty,
                });
            }

            Status = external?.HasForwardToken == true ? "已配置 token" : "未配置 token";
        }
        catch (Exception ex)
        {
            Status = $"加载失败：{ex.Message}";
        }
    }

    public void AddReverse()
    {
        ReverseServers.Add(new ReverseServerItem());
    }

    public void RemoveReverse(ReverseServerItem item)
    {
        ReverseServers.Remove(item);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var sessions = AllowedSessions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var reverse = ReverseServers.Select(s => new
            {
                host = s.Host,
                port = (int)s.Port,
                token = string.IsNullOrWhiteSpace(s.Token) ? null : s.Token.Trim(),
            }).ToList();

            await _api.CallAsync<object>("setExternal", new
            {
                allowSend = AllowSend,
                allowedSessions = sessions,
                forwardUrl = ForwardUrl.Trim(),
                forwardToken = string.IsNullOrWhiteSpace(ForwardToken) ? null : ForwardToken.Trim(),
                reverseServers = reverse,
            }).ConfigureAwait(true);

            ForwardToken = string.Empty;
            Status = "已保存（反向监听改动需重启服务端生效）";
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }
}
