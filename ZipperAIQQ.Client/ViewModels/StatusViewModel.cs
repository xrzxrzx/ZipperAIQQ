using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>状态页 VM：连接信息 + 运行状态 + 重启入口（#36）。</summary>
public sealed partial class StatusViewModel : ObservableObject
{
    private readonly ServerApiClient _api;
    private readonly AppSettings _settings;
    private readonly SessionsViewModel _sessions;

    public StatusViewModel(ServerApiClient api, AppSettings settings, SessionsViewModel sessions)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sessions);

        _api = api;
        _settings = settings;
        _sessions = sessions;

        ServerUrl = settings.ServerUrl;
        Token = settings.Token;
    }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "http://127.0.0.1:5180";

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusLine { get; set; } = "填好服务器地址后点「连接」。";

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        try
        {
            _settings.ServerUrl = ServerUrl.Trim();
            _settings.Token = Token.Trim();
            _settings.Save();

            StatusLine = "连接中…";
            var data = await _api.CallAsync<PingDto>("ping").ConfigureAwait(true);
            if (data is null)
            {
                throw new InvalidOperationException("ping 没有返回数据。");
            }

            await LoadStatusCoreAsync().ConfigureAwait(true);

            // 顺手拉一次会话列表：人设页要靠它出候选
            try
            {
                await _sessions.LoadSessionsCommand.ExecuteAsync(null).ConfigureAwait(true);
            }
            catch
            {
                // 会话列表拉不到不影响连接
            }

            StatusLine = $"已连接（服务端版本 {data.ServerVersion}）";
        }
        catch (Exception ex)
        {
            StatusLine = $"连接失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        try
        {
            StatusLine = "刷新状态中…";
            await LoadStatusCoreAsync().ConfigureAwait(true);
            StatusLine = "状态已刷新";
        }
        catch (Exception ex)
        {
            StatusLine = $"刷新状态失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RestartServerAsync()
    {
        try
        {
            await _api.CallAsync<object>("restartServer").ConfigureAwait(true);
            StatusLine = "已请求重启服务端：约 0.5 秒后停止，由守护脚本重新拉起。";
        }
        catch (Exception ex)
        {
            StatusLine = $"重启失败：{ex.Message}";
        }
    }

    /// <summary>心跳用：静默刷新，失败不打扰用户。</summary>
    public async Task RefreshQuietAsync()
    {
        if (string.IsNullOrEmpty(StatusText))
        {
            return;
        }

        try
        {
            await LoadStatusCoreAsync().ConfigureAwait(true);
        }
        catch
        {
            // 心跳失败由状态条体现
        }
    }

    private async Task LoadStatusCoreAsync()
    {
        var status = await _api.CallAsync<StatusDto>("getStatus").ConfigureAwait(true);
        if (status is null)
        {
            throw new InvalidOperationException("getStatus 没有返回数据。");
        }

        var uptime = TimeSpan.FromSeconds(status.UptimeSeconds);
        StatusText =
            $"模型：{status.Model}\n" +
            $"通道：{(status.ChannelConnected ? "已连接" : "未连接")}\n" +
            $"运行时长：{(int)uptime.TotalHours} 小时 {uptime.Minutes} 分\n" +
            $"会话数：{status.Sessions}\n" +
            $"待执行定时任务：{status.PendingTasks}\n" +
            $"监听地址：{status.HttpAddress}";
    }
}
