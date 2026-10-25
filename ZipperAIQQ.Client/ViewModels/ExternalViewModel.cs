using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>一个外部 WS 配置块（反向或正向）。</summary>
public sealed partial class ExternalServerItem : ObservableObject
{
    public required string Kind { get; init; }

    public bool IsReverse => Kind == "reverse";

    public string TypeText => IsReverse ? "Websocket 服务器" : "Websocket 客户端";

    public string EndpointText => IsReverse ? $"{Host}:{Port:0}" : Url;

    public string TokenText => string.IsNullOrWhiteSpace(Token) ? "未设置 Token" : "已设置 Token";

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    [ObservableProperty]
    public partial string Host { get; set; } = "0.0.0.0";

    [ObservableProperty]
    public partial double Port { get; set; } = 5181;

    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;

    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(EndpointText));

    partial void OnPortChanged(double value) => OnPropertyChanged(nameof(EndpointText));

    partial void OnUrlChanged(string value) => OnPropertyChanged(nameof(EndpointText));

    partial void OnTokenChanged(string value) => OnPropertyChanged(nameof(TokenText));
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

    public ObservableCollection<ExternalServerItem> AllServers { get; } = [];

    public ObservableCollection<ExternalServerItem> ReverseServers { get; } = [];

    public ObservableCollection<ExternalServerItem> ForwardServers { get; } = [];

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

            AllServers.Clear();
            ReverseServers.Clear();
            ForwardServers.Clear();

            foreach (var item in external?.ReverseServers ?? [])
            {
                var server = new ExternalServerItem
                {
                    Kind = "reverse",
                    Name = string.IsNullOrWhiteSpace(item.Name) ? "反向监听" : item.Name,
                    Enabled = item.Enabled,
                    Host = item.Host,
                    Port = item.Port,
                    Token = item.Token,
                };

                AllServers.Add(server);
                ReverseServers.Add(server);
            }

            foreach (var item in external?.ForwardServers ?? [])
            {
                var server = new ExternalServerItem
                {
                    Kind = "forward",
                    Name = string.IsNullOrWhiteSpace(item.Name) ? "正向连接" : item.Name,
                    Enabled = item.Enabled,
                    Url = item.Url,
                    Token = item.Token,
                };

                AllServers.Add(server);
                ForwardServers.Add(server);
            }

            Status = "已加载";
        }
        catch (Exception ex)
        {
            Status = $"加载失败：{ex.Message}";
        }
    }

    public ExternalServerItem AddReverse()
    {
        var server = new ExternalServerItem
        {
            Kind = "reverse",
            Name = "反向监听",
            Host = "0.0.0.0",
            Port = 5181,
        };

        AllServers.Add(server);
        ReverseServers.Add(server);
        return server;
    }

    public ExternalServerItem AddForward()
    {
        var server = new ExternalServerItem
        {
            Kind = "forward",
            Name = "正向连接",
            Url = "ws://",
        };

        AllServers.Add(server);
        ForwardServers.Add(server);
        return server;
    }

    public void Remove(ExternalServerItem item)
    {
        AllServers.Remove(item);
        ReverseServers.Remove(item);
        ForwardServers.Remove(item);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var sessions = AllowedSessions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var reverse = AllServers
                .Where(s => s.IsReverse)
                .Select(s => new
                {
                    name = s.Name.Trim(),
                    host = s.Host.Trim(),
                    port = (int)s.Port,
                    token = s.Token.Trim(),
                    enabled = s.Enabled,
                })
                .ToList();

            var forward = AllServers
                .Where(s => !s.IsReverse)
                .Select(s => new
                {
                    name = s.Name.Trim(),
                    url = s.Url.Trim(),
                    token = s.Token.Trim(),
                    enabled = s.Enabled,
                })
                .ToList();

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
