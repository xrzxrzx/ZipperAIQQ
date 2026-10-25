using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>反向 WS 监听块（页面里可增删）。</summary>
public sealed partial class ReverseServerItem : ObservableObject
{
    [ObservableProperty]
    public partial string Host { get; set; } = "0.0.0.0";

    [ObservableProperty]
    public partial double Port { get; set; } = 5181;

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasToken { get; set; }
}

/// <summary>正向 WS 目标块。</summary>
public sealed partial class ForwardServerItem : ObservableObject
{
    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasToken { get; set; }
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

    public ObservableCollection<ForwardServerItem> ForwardServers { get; } = [];

    [ObservableProperty]
    public partial bool AllowSend { get; set; }

    [ObservableProperty]
    public partial string AllowedSessions { get; set; } = string.Empty;

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

            ReverseServers.Clear();
            foreach (var item in external?.ReverseServers ?? [])
            {
                ReverseServers.Add(new ReverseServerItem
                {
                    Host = item.Host,
                    Port = item.Port,
                    Token = string.Empty,
                    HasToken = item.HasToken,
                });
            }

            ForwardServers.Clear();
            foreach (var item in external?.ForwardServers ?? [])
            {
                ForwardServers.Add(new ForwardServerItem
                {
                    Url = item.Url,
                    Token = string.Empty,
                    HasToken = item.HasToken,
                });
            }

            Status = "已加载";
        }
        catch (Exception ex)
        {
            Status = $"加载失败：{ex.Message}";
        }
    }

    public void AddReverse() => ReverseServers.Add(new ReverseServerItem());

    public void RemoveReverse(ReverseServerItem item) => ReverseServers.Remove(item);

    public void AddForward() => ForwardServers.Add(new ForwardServerItem());

    public void RemoveForward(ForwardServerItem item) => ForwardServers.Remove(item);

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var sessions = AllowedSessions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var reverse = ReverseServers.Select(s => new
            {
                host = s.Host.Trim(),
                port = (int)s.Port,
                token = string.IsNullOrWhiteSpace(s.Token) ? null : s.Token.Trim(),
            }).ToList();

            var forward = ForwardServers.Select(s => new
            {
                url = s.Url.Trim(),
                token = string.IsNullOrWhiteSpace(s.Token) ? null : s.Token.Trim(),
            }).ToList();

            await _api.CallAsync<object>("setExternal", new
            {
                allowSend = AllowSend,
                allowedSessions = sessions,
                reverseServers = reverse,
                forwardServers = forward,
            }).ConfigureAwait(true);

            Status = "已保存（反向端口改动需重启生效；正向会立即重连）";
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }
}
